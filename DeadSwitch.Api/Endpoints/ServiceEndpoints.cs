using DeadSwitch.Api.Requests;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Api.Endpoints;

// ASP.NET Core fills the method parameters by itself: id from the route, request from the JSON
// body, services from dependency injection, and ct is cancelled if the client disconnects.
public static class ServiceEndpoints
{
    // The error message for the history repeats the two limits on purpose: it is a fixed text.
    private const int DefaultHistoryTake = 50;
    private const int MinHistoryTake = 1;
    private const int MaxHistoryTake = 200;

    private const int CheckUrlTimeoutSeconds = 10;

    // {id:guid} makes a route with an id that is not a Guid return 404.
    public static void Map(WebApplication app)
    {
        app.MapGet("/health", GetHealth);
        app.MapPost("/services", RegisterServiceAsync);
        app.MapGet("/services", GetAllServicesAsync);
        app.MapGet("/services/overview", GetOverviewAsync);
        app.MapGet("/services/{id:guid}", GetServiceAsync);
        app.MapPut("/services/{id:guid}", UpdateServiceAsync);
        app.MapDelete("/services/{id:guid}", DeleteServiceAsync);
        app.MapPost("/services/{id:guid}/check", CheckServiceAsync);
        app.MapGet("/services/{id:guid}/history", GetHistoryAsync);
        app.MapGet("/check", CheckUrlAsync);
    }

    private static IResult GetHealth()
    {
        return Results.Ok(new { status = "UP", service = "DeadSwitch" });
    }

    private static async Task<IResult> RegisterServiceAsync(
        RegisterServiceRequest request,
        MonitoredServiceService monitoring,
        CancellationToken ct)
    {
        MonitoredService created = await monitoring.RegisterAsync(request.ToConfiguration(), ct);
        return Results.Created($"/services/{created.Id}", created);
    }

    private static async Task<IResult> GetAllServicesAsync(MonitoredServiceService monitoring, CancellationToken ct)
    {
        List<MonitoredService> services = await monitoring.GetAllAsync(ct);
        return Results.Ok(services);
    }

    private static async Task<IResult> GetOverviewAsync(MonitoredServiceService monitoring, CancellationToken ct)
    {
        List<ServiceOverview> overview = await monitoring.GetOverviewAsync(ct);
        return Results.Ok(overview);
    }

    private static async Task<IResult> GetServiceAsync(Guid id, MonitoredServiceService monitoring, CancellationToken ct)
    {
        MonitoredService? found = await monitoring.GetByIdAsync(id, ct);
        if (found == null)
        {
            return Results.NotFound();
        }

        return Results.Ok(found);
    }

    private static async Task<IResult> UpdateServiceAsync(
        Guid id,
        UpdateServiceRequest request,
        MonitoredServiceService monitoring,
        CancellationToken ct)
    {
        MonitoredService? updated = await monitoring.UpdateAsync(id, request.ToConfiguration(), ct);
        if (updated == null)
        {
            return Results.NotFound();
        }

        return Results.Ok(updated);
    }

    private static async Task<IResult> DeleteServiceAsync(Guid id, MonitoredServiceService monitoring, CancellationToken ct)
    {
        bool deleted = await monitoring.DeleteAsync(id, ct);
        if (!deleted)
        {
            return Results.NotFound();
        }

        return Results.NoContent();
    }

    private static async Task<IResult> CheckServiceAsync(Guid id, MonitoredServiceService monitoring, CancellationToken ct)
    {
        CheckRecord? record = await monitoring.CheckAsync(id, ct: ct);
        if (record == null)
        {
            return Results.NotFound();
        }

        return Results.Ok(record);
    }

    private static async Task<IResult> GetHistoryAsync(
        Guid id,
        int? skip,
        int? take,
        MonitoredServiceService monitoring,
        CancellationToken ct)
    {
        int offset = skip ?? 0;
        int limit = take ?? DefaultHistoryTake;

        if (offset < 0 || limit < MinHistoryTake || limit > MaxHistoryTake)
        {
            return Results.Problem(statusCode: 400, detail: "skip must be nonnegative and take must be between 1 and 200.");
        }

        MonitoredService? found = await monitoring.GetByIdAsync(id, ct);
        if (found == null)
        {
            return Results.NotFound();
        }

        List<CheckRecord> history = await monitoring.GetHistoryAsync(id, offset, limit, ct);
        return Results.Ok(history);
    }

    private static async Task<IResult> CheckUrlAsync(string url, IHealthProbe probe, HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";

        // Validated here, because the probe turns a rejected URL into UNKNOWN and this route must return 400.
        UrlSafety.Validate(url);

        ProbeResult result = await probe.CheckAsync(url, CheckUrlTimeoutSeconds, ct);

        return Results.Ok(new
        {
            url,
            status = result.Status,
            statusCode = result.StatusCode,
            responseTimeMs = result.ResponseTimeMs,
            failureReason = result.FailureReason
        });
    }
}
