using DeadSwitch.Api.Monitoring;
using DeadSwitch.Api.Requests;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Infrastructure.Data;
using DeadSwitch.Infrastructure.Monitoring;
using DeadSwitch.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ServiceLocks>();
builder.Services.AddDbContext<DeadSwitchDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DeadSwitchDatabase")
        ?? throw new InvalidOperationException("DeadSwitchDatabase connection string is required.")));
builder.Services.AddScoped<IMonitoredServiceRepository, MonitoredServiceRepository>();
builder.Services.AddScoped<MonitoredServiceService>();
builder.Services.AddHttpClient<IHealthProbe, HttpHealthProbe>(client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DeadSwitch/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseProxy = false,
    UseCookies = false,
    ConnectCallback = PublicInternetConnector.ConnectAsync,
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    ConnectTimeout = TimeSpan.FromSeconds(10)
}).RemoveAllLoggers();
builder.Services.AddOptions<MonitoringOptions>().BindConfiguration("Monitoring")
    .Validate(x => x.PollIntervalSeconds is >= 1 and <= 3600, "PollIntervalSeconds must be between 1 and 3600.")
    .ValidateOnStart();
builder.Services.AddHostedService<MonitoringWorker>();
var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (ArgumentException ex)
    {
        await Results.Problem(statusCode: 400, title: "Invalid configuration", detail: ex.Message).ExecuteAsync(context);
    }
    catch (CheckInProgressException ex)
    {
        await Results.Problem(statusCode: 409, title: "Check in progress", detail: ex.Message).ExecuteAsync(context);
    }
});
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapGet("/health", () => Results.Ok(new { status = "UP", service = "DeadSwitch" }));
app.MapPost("/services", async (RegisterServiceRequest request, MonitoredServiceService service, CancellationToken ct) =>
{
    var created = await service.RegisterAsync(request.ToConfiguration(), ct);
    return Results.Created($"/services/{created.Id}", created);
});
app.MapGet("/services", async (MonitoredServiceService service, CancellationToken ct) => Results.Ok(await service.GetAllAsync(ct)));
app.MapGet("/services/{id:guid}", async (Guid id, MonitoredServiceService service, CancellationToken ct) =>
    await service.GetByIdAsync(id, ct) is { } item ? Results.Ok(item) : Results.NotFound());
app.MapPut("/services/{id:guid}", async (Guid id, RegisterServiceRequest request, MonitoredServiceService service, CancellationToken ct) =>
    await service.UpdateAsync(id, request.ToConfiguration(), ct) is { } item ? Results.Ok(item) : Results.NotFound());
app.MapDelete("/services/{id:guid}", async (Guid id, MonitoredServiceService service, CancellationToken ct) =>
    await service.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
app.MapPost("/services/{id:guid}/check", async (Guid id, MonitoredServiceService service, CancellationToken ct) =>
    await service.CheckAsync(id, ct: ct) is { } record ? Results.Ok(record) : Results.NotFound());
app.MapGet("/services/{id:guid}/history", async (Guid id, int? skip, int? take, MonitoredServiceService service, CancellationToken ct) =>
{
    var offset = skip ?? 0;
    var limit = take ?? 50;
    if (offset < 0 || limit is < 1 or > 200)
        return Results.Problem(statusCode: 400, detail: "skip must be nonnegative and take must be between 1 and 200.");
    if (await service.GetByIdAsync(id, ct) is null) return Results.NotFound();
    return Results.Ok(await service.GetHistoryAsync(id, offset, limit, ct));
});
// Compatibility with the original read-triggered checks; use POST for new clients.
app.MapGet("/services/{id:guid}/check", async (Guid id, MonitoredServiceService service, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var record = await service.CheckAsync(id, ct: ct);
    if (record is null) return Results.NotFound();
    var item = await service.GetByIdAsync(id, ct);
    return Results.Ok(new { id, name = item?.Name, url = record.Url, status = record.Status,
        record.StatusCode, record.ResponseTimeMs, lastCheckedAt = record.CheckedAtUtc, record.FailureReason });
});
app.MapGet("/check", async (string url, IHealthProbe probe, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    UrlSafety.Validate(url);
    var result = await probe.CheckAsync(url, 10, ct);
    return Results.Ok(new { url, status = result.Status, result.StatusCode, result.ResponseTimeMs, result.FailureReason });
});
app.Run();
public partial class Program { }
