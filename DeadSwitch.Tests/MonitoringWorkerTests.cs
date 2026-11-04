using System.Net.Http.Json;
using DeadSwitch.Api.Monitoring;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

public class MonitoringWorkerTests
{
    [Fact]
    public async Task WorkerChecksOnlyEnabledServicesThatAreDue()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await app.RegisterServiceAsync(client, new ServiceConfiguration("Enabled", "https://example.com", IntervalSeconds: 10));
        await app.RegisterServiceAsync(client, new ServiceConfiguration("Paused", "https://example.org", IsEnabled: false));
        using MonitoringWorker worker = app.CreateWorker();

        await worker.RunPassAsync(CancellationToken.None);
        Assert.Equal(1, app.Probe.Calls);

        // The clock has not moved, so the enabled service is not due yet.
        await worker.RunPassAsync(CancellationToken.None);
        Assert.Equal(1, app.Probe.Calls);

        List<MonitoredService> services = (await client.GetFromJsonAsync<List<MonitoredService>>("/services"))!;
        MonitoredService enabled = services.Single(service => service.IsEnabled);
        MonitoredService paused = services.Single(service => !service.IsEnabled);
        Assert.Equal("UP", enabled.CurrentStatus);
        Assert.Null(paused.LastCheckedAt);
    }

    [Fact]
    public async Task NewWorkerUsesSavedTimeOfLastCheckAfterRestart()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await app.RegisterServiceAsync(client, new ServiceConfiguration("Enabled", "https://example.com", IntervalSeconds: 10));
        using MonitoringWorker worker = app.CreateWorker();
        await worker.RunPassAsync(CancellationToken.None);
        Assert.Equal(1, app.Probe.Calls);

        // A new worker has no memory of its own, so it can only know this from the database.
        using MonitoringWorker restartedWorker = app.CreateWorker();
        await restartedWorker.RunPassAsync(CancellationToken.None);
        Assert.Equal(1, app.Probe.Calls);

        app.Clock.Now = app.Clock.Now.AddSeconds(11);
        await restartedWorker.RunPassAsync(CancellationToken.None);
        Assert.Equal(2, app.Probe.Calls);
    }
}
