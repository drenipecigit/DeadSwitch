using System.Net;
using System.Net.Http.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace DeadSwitch.Tests;

public class ServiceCheckTests
{
    [Fact]
    public async Task ManualCheckSavesStatusAndHistory()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        ServiceConfiguration configuration = new ServiceConfiguration("Example", "https://example.com", IsEnabled: false);
        MonitoredService created = await app.RegisterServiceAsync(client, configuration);

        HttpResponseMessage check = await client.PostAsync($"/services/{created.Id}/check", null);

        check.EnsureSuccessStatusCode();
        CheckRecord record = (await check.Content.ReadFromJsonAsync<CheckRecord>())!;
        Assert.Equal("UP", record.Status);
        Assert.Equal(200, record.StatusCode);

        MonitoredService persisted = (await client.GetFromJsonAsync<MonitoredService>($"/services/{created.Id}"))!;
        Assert.Equal("UP", persisted.CurrentStatus);
        Assert.NotNull(persisted.LastCheckedAt);
        Assert.Equal(record.CheckedAtUtc, persisted.LastCheckedAt.Value.UtcDateTime);

        List<CheckRecord> history = (await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{created.Id}/history"))!;
        CheckRecord onlyRecord = Assert.Single(history);
        Assert.Equal(record.Id, onlyRecord.Id);
    }

    [Fact]
    public async Task HistoryIsPagedWithNewestCheckFirst()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        ServiceConfiguration configuration = new ServiceConfiguration("Example", "https://example.com", IsEnabled: false);
        MonitoredService created = await app.RegisterServiceAsync(client, configuration);
        HttpResponseMessage firstCheck = await client.PostAsync($"/services/{created.Id}/check", null);
        firstCheck.EnsureSuccessStatusCode();

        app.Clock.Now = app.Clock.Now.AddSeconds(1);
        app.Probe.Result = new ProbeResult("DOWN", 503, 31, "HTTP 503");
        HttpResponseMessage secondCheck = await client.PostAsync($"/services/{created.Id}/check", null);
        secondCheck.EnsureSuccessStatusCode();

        List<CheckRecord> firstPage = (await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{created.Id}/history?take=1"))!;
        CheckRecord newest = Assert.Single(firstPage);
        Assert.Equal("DOWN", newest.Status);

        List<CheckRecord> secondPage = (await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{created.Id}/history?skip=1&take=1"))!;
        Assert.Single(secondPage);
    }

    [Fact]
    public async Task OverlappingCheckOfSameServiceReturns409()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", "https://example.com"));
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        MonitoredServiceService services = scope.ServiceProvider.GetRequiredService<MonitoredServiceService>();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        TaskCompletionSource started = HangProbeUntilCancelled(app.Probe);

        Task<CheckRecord?> firstCheck = services.CheckAsync(service.Id, ct: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        HttpResponseMessage secondCheck = await client.PostAsync($"/services/{service.Id}/check", null);

        Assert.Equal(HttpStatusCode.Conflict, secondCheck.StatusCode);

        // Let the hanging check finish, so it does not outlive the test.
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstCheck);
    }

    [Fact]
    public async Task CancelledCheckSavesNothingAndReleasesTheLock()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", "https://example.com"));
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        MonitoredServiceService services = scope.ServiceProvider.GetRequiredService<MonitoredServiceService>();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        TaskCompletionSource started = HangProbeUntilCancelled(app.Probe);

        Task<CheckRecord?> firstCheck = services.CheckAsync(service.Id, ct: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstCheck);
        List<CheckRecord> history = (await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{service.Id}/history"))!;
        Assert.Empty(history);

        // A new check can only succeed if the cancelled check released its lock.
        app.Probe.Check = null;
        HttpResponseMessage nextCheck = await client.PostAsync($"/services/{service.Id}/check", null);
        nextCheck.EnsureSuccessStatusCode();
    }

    // Makes the probe hang until cancelled. The returned source completes when the probe has started,
    // so the test knows the first check is running and holding its lock.
    private static TaskCompletionSource HangProbeUntilCancelled(TestProbe probe)
    {
        // Without this option, the test's own code could continue inside the probe's call to SetResult.
        TaskCompletionSource started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        probe.Check = async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return probe.Result;
        };

        return started;
    }
}
