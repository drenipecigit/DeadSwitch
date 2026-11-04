using DeadSwitch.Application.Services;
using Microsoft.Extensions.Options;

namespace DeadSwitch.Api.Monitoring;

public sealed class MonitoringOptions
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 5;
}

public sealed class MonitoringWorker(IServiceScopeFactory scopes, IOptions<MonitoringOptions> options,
    ILogger<MonitoringWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds));
        try
        {
            do
            {
                try { await RunPassAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogError(ex, "Monitoring pass failed; retrying on next poll."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task RunPassAsync(CancellationToken ct)
    {
        List<Guid> ids;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var services = await scope.ServiceProvider.GetRequiredService<MonitoredServiceService>().GetAllAsync(ct);
            ids = services.Where(x => x.IsEnabled).Select(x => x.Id).ToList();
        }
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MonitoredServiceService>().CheckAsync(id, onlyIfDue: true, ct: ct);
            }
            catch (CheckInProgressException) { }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Monitoring failed for service {ServiceId}.", id); }
        }
    }
}
