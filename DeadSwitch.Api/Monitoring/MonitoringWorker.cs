using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using Microsoft.Extensions.Options;

namespace DeadSwitch.Api.Monitoring;

// A BackgroundService runs for as long as the application does, and is created only once.
public sealed class MonitoringWorker : BackgroundService
{
    // A constant, because the tests wait for this exact log line to know that a pass is done.
    public const string PassFinishedMessage = "Monitoring pass finished.";

    // The worker lives forever but the DbContext must be short-lived, so it cannot be injected
    // directly. The worker creates a new scope, and with it a new DbContext, for each piece of work.
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<MonitoringOptions> _options;
    private readonly ILogger<MonitoringWorker> _logger;

    // TimeProvider instead of the system clock, so the tests can move the timer by hand.
    private readonly TimeProvider _clock;

    public MonitoringWorker(
        IServiceScopeFactory scopes,
        IOptions<MonitoringOptions> options,
        ILogger<MonitoringWorker> logger,
        TimeProvider clock)
    {
        _scopes = scopes;
        _options = options;
        _logger = logger;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            return;
        }

        // PeriodicTimer waits without blocking a thread, and never starts a new tick
        // while the previous pass is still running.
        TimeSpan pollInterval = TimeSpan.FromSeconds(_options.Value.PollIntervalSeconds);
        using PeriodicTimer timer = new PeriodicTimer(pollInterval, _clock);

        try
        {
            // "do" and not "while": the first pass must run at once, and not wait one poll for the first tick.
            do
            {
                await RunPassAndLogFailureAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The application is shutting down. This is the normal way to stop, not an error.
        }
    }

    // Public so the tests can run one pass without waiting for the timer.
    public async Task RunPassAsync(CancellationToken ct)
    {
        List<Guid> ids = await GetEnabledServiceIdsAsync(ct);

        // Only the ids are kept, so every check reads fresh data in its own scope.
        foreach (Guid id in ids)
        {
            ct.ThrowIfCancellationRequested();
            await CheckServiceAndLogFailureAsync(id, ct);
        }
    }

    // "when" cannot be replaced by an if inside the catch without worse code. The next catch logs
    // every other failure, so the if would need a second copy of that log line. With "when", an
    // OperationCanceledException that is not caused by shutdown falls through and is logged too.
    private async Task RunPassAndLogFailureAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunPassAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Catches everything on purpose: one failed pass must not stop the monitoring.
            _logger.LogError(ex, "Monitoring pass failed; retrying on next poll.");
        }

        _logger.LogDebug(PassFinishedMessage);
    }

    private async Task<List<Guid>> GetEnabledServiceIdsAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        MonitoredServiceService monitoring = scope.ServiceProvider.GetRequiredService<MonitoredServiceService>();
        List<MonitoredService> services = await monitoring.GetAllAsync(ct);

        List<Guid> ids = new List<Guid>();
        foreach (MonitoredService monitoredService in services)
        {
            if (monitoredService.IsEnabled)
            {
                ids.Add(monitoredService.Id);
            }
        }

        return ids;
    }

    private async Task CheckServiceAndLogFailureAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
            MonitoredServiceService monitoring = scope.ServiceProvider.GetRequiredService<MonitoredServiceService>();
            await monitoring.CheckAsync(id, onlyIfDue: true, ct: ct);
        }
        catch (CheckInProgressException)
        {
            // Someone else holds the lock right now. The service is checked on a later pass.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A shutdown must stop the pass, so it is passed on. The catch below logs any other cancellation.
            throw;
        }
        catch (Exception ex)
        {
            // Catches everything on purpose: one failing service must not stop the others.
            _logger.LogError(ex, "Monitoring failed for service {ServiceId}.", id);
        }
    }
}
