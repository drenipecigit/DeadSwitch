using System.Text.Json;
using System.Text.Json.Serialization;
using DeadSwitch.Api.Endpoints;
using DeadSwitch.Api.Json;
using DeadSwitch.Api.Monitoring;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Infrastructure.Data;
using DeadSwitch.Infrastructure.Monitoring;
using DeadSwitch.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Makes error responses use the standard Problem Details JSON format.
builder.Services.AddProblemDetails();

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

builder.Services.AddHttpClient<IHealthProbe, HttpHealthProbe>(client =>
{
    // HttpClient's default limit of 100 seconds is turned off, because every service has its
    // own timeout. HttpHealthProbe enforces that one with a CancellationToken.
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DeadSwitch/1.0");
})
    // Security-critical: the handler is what blocks private addresses and redirects.
    .ConfigurePrimaryHttpMessageHandler(() => ProtectedHttpHandler.Create())
    // Removes HttpClient's automatic logging, which writes every URL to the log on every check.
    .RemoveAllLoggers();

builder.Services.AddOptions<MonitoringOptions>()
    .BindConfiguration("Monitoring")
    .Validate(MonitoringOptions.HasValidPollInterval, "PollIntervalSeconds must be between 1 and 3600.")
    // Without this, bad configuration is only discovered when the options are first used.
    // With it, the application refuses to start.
    .ValidateOnStart();

builder.Services.AddHostedService<MonitoringWorker>();

WebApplication app = builder.Build();

// Turns unexpected exceptions into a 500 response in the Problem Details format.
app.UseExceptionHandler();

// Translates the exceptions the Application layer uses for expected errors into status codes.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (ServiceValidationException ex)
    {
        // Safe to show: this exception only ever holds fixed texts that we wrote ourselves.
        IResult problem = Results.Problem(statusCode: 400, title: "Invalid configuration", detail: ex.Message);
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

app.Run();

// The compiler generates the Program class as internal when top-level statements are used.
// Making it public lets the test project start the API with WebApplicationFactory<Program>.
public partial class Program
{
}
