using System.Text.Json;
using System.Text.Json.Serialization;
using DeadSwitch.Api.Endpoints;
using DeadSwitch.Api.Json;
using DeadSwitch.Api.Monitoring;
using DeadSwitch.Api.Security;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Infrastructure.Data;
using DeadSwitch.Infrastructure.Monitoring;
using DeadSwitch.Infrastructure.Notifications;
using DeadSwitch.Infrastructure.Repositories;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Makes error responses use the standard Problem Details JSON format.
builder.Services.AddProblemDetails();

// Otherwise a bad request is only thrown in Development. In Production the framework answers on its
// own with an empty body, so the two environments would behave differently.
builder.Services.Configure<RouteHandlerOptions>(options =>
{
    options.ThrowOnBadRequest = true;
});

// Clients read status as "UP", "DOWN" and "UNKNOWN". Without this, an enum becomes a number in JSON.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));

    // Clients read every time in UTC with Z. Without this, a DateTimeOffset ends with +00:00.
    options.SerializerOptions.Converters.Add(new UtcDateTimeOffsetConverter());
});

// TimeProvider instead of DateTime.UtcNow, so the tests can replace the clock with a fake.
builder.Services.AddSingleton(TimeProvider.System);

// Singleton, because the locks only work if the API and the worker share the same instance.
builder.Services.AddSingleton<ServiceLocks>();

builder.Services.AddDbContext<DeadSwitchDbContext>(options =>
{
    string? connectionString = builder.Configuration.GetConnectionString("DeadSwitchDatabase");
    if (connectionString == null)
    {
        throw new InvalidOperationException("DeadSwitchDatabase connection string is required.");
    }

    options.UseSqlite(connectionString);
});

// Scoped, because they use the DbContext, which must live for one request and no longer.
builder.Services.AddScoped<IMonitoredServiceRepository, MonitoredServiceRepository>();
builder.Services.AddScoped<MonitoredServiceService>();

// Singleton, because the factory replaces the handler now and then, and the probe must be able to read
// what the handler noted.
builder.Services.AddSingleton<TlsHandshakeRecorder>();

builder.Services.AddHttpClient<IHealthProbe, HttpHealthProbe>(client =>
{
    // HttpClient's default limit of 100 seconds is turned off, because every service has its
    // own timeout. HttpHealthProbe enforces that one with a CancellationToken.
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DeadSwitch/1.0");
})
    // Security-critical: the handler is what blocks private addresses and redirects.
    .ConfigurePrimaryHttpMessageHandler(CreateProbeHandler)
    // Removes HttpClient's automatic logging, which writes every URL to the log on every check.
    .RemoveAllLoggers();

// The address is checked at startup, so a wrong one stops the application here and not at the first
// alarm. The text shown is fixed and never holds the address.
builder.Services.AddOptions<DiscordOptions>()
    .BindConfiguration("Discord")
    .Validate(DiscordOptions.HasValidWebhookUrl, DiscordOptions.InvalidWebhookUrlMessage)
    .ValidateOnStart();

builder.Services.AddHttpClient<INotifier, DiscordNotifier>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DeadSwitch/1.0");
})
    // The same protected handler as the probe, so there is no new way out of the application:
    // no redirects can move the message, no proxy sees it, and private addresses stay blocked.
    .ConfigurePrimaryHttpMessageHandler(() => ProtectedHttpHandler.Create())
    // Security-critical: HttpClient's automatic logging writes the whole address, and the token in it
    // is all anyone needs to post to the channel.
    .RemoveAllLoggers();

builder.Services.AddOptions<MonitoringOptions>()
    .BindConfiguration("Monitoring")
    .Validate(MonitoringOptions.HasValidPollInterval, "PollIntervalSeconds must be between 1 and 3600.")
    // Without this, bad configuration is only discovered when the options are first used.
    // With it, the application refuses to start.
    .ValidateOnStart();

builder.Services.AddOptions<SecurityWarningSettings>()
    .BindConfiguration("SecurityWarnings")
    .Validate(SecurityWarningSettings.HasValidWarningDays, "CertificateWarningDays must be between 1 and 365.")
    .ValidateOnStart();

// Registered as a plain object, so that Application does not need the options package.
builder.Services.AddSingleton(provider => provider.GetRequiredService<IOptions<SecurityWarningSettings>>().Value);
builder.Services.AddScoped<SecurityWarningFinder>();

builder.Services.AddHostedService<MonitoringWorker>();

WebApplication app = builder.Build();

DiscordOptions discordOptions = app.Services.GetRequiredService<IOptions<DiscordOptions>>().Value;
if (string.IsNullOrEmpty(discordOptions.WebhookUrl))
{
    app.Logger.LogInformation(DiscordOptions.NotificationsOffMessage);
}
else
{
    app.Logger.LogInformation(DiscordOptions.NotificationsOnMessage);
}

// First, so that every answer gets the headers, also one from the exception handler below.
app.Use(SecurityHeaders.AddAsync);

// Turns unexpected exceptions into a 500 response in the Problem Details format.
app.UseExceptionHandler();

// Serves the dashboard from wwwroot: "/" becomes index.html.
app.UseDefaultFiles();
StaticFileOptions dashboardFiles = new StaticFileOptions();
dashboardFiles.OnPrepareResponse = RequireCheckBeforeReuse;
app.UseStaticFiles(dashboardFiles);

// After the files on purpose: they hold no data and change nothing, and a link to the page must work.
app.Use(CrossSiteRequests.BlockAsync);

const string UnreadableRequestTitle = "Invalid request";
const string UnreadableRequestDetail = "The request could not be read.";

// Translates the exceptions the Application layer uses for expected errors into status codes.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);

        // A wrong content type throws nothing: the framework only sets 415 and leaves the body empty.
        // It gets the same fixed answer here, so that every error has the same format.
        if (context.Response.StatusCode == StatusCodes.Status415UnsupportedMediaType && !context.Response.HasStarted)
        {
            IResult problem = Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: UnreadableRequestTitle, detail: UnreadableRequestDetail);
            await problem.ExecuteAsync(context);
        }
    }
    catch (ServiceValidationException ex)
    {
        // Safe to show: this exception only ever holds fixed texts that we wrote ourselves.
        IResult problem = Results.Problem(statusCode: 400, title: "Invalid configuration", detail: ex.Message);
        await problem.ExecuteAsync(context);
    }
    catch (BadHttpRequestException ex)
    {
        // Fixed text on purpose: ex.Message names the parameter and the class, and the inner exception
        // holds the JSON path and position. The status code is kept because it is not always 400:
        // a body that is too large gives 413.
        IResult problem = Results.Problem(statusCode: ex.StatusCode, title: UnreadableRequestTitle, detail: UnreadableRequestDetail);
        await problem.ExecuteAsync(context);
    }
    catch (CheckInProgressException ex)
    {
        IResult problem = Results.Problem(statusCode: 409, title: "Check in progress", detail: ex.Message);
        await problem.ExecuteAsync(context);
    }
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

ServiceEndpoints.Map(app);

app.Lifetime.ApplicationStarted.Register(() => LocalOnlyCheck.StopIfListeningBeyondLoopback(app));

app.Run();

static HttpMessageHandler CreateProbeHandler(IServiceProvider services)
{
    TlsHandshakeRecorder recorder = services.GetRequiredService<TlsHandshakeRecorder>();
    return ProtectedHttpHandler.Create(recorder);
}

// no-cache means "ask the server first", so that a browser never keeps running an old script.
static void RequireCheckBeforeReuse(StaticFileResponseContext file)
{
    file.Context.Response.Headers.CacheControl = "no-cache";
}

// The compiler generates the Program class as internal when top-level statements are used.
// Making it public lets the test project start the API with WebApplicationFactory<Program>.
public partial class Program
{
}
