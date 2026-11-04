using System.Net;
using System.Net.Http.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
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
        CheckRecord record = (await check.Content.ReadFromJsonAsync<CheckRecord>(TestJson.Options))!;
        Assert.Equal(ServiceStatus.Up, record.Status);
        Assert.Equal(200, record.StatusCode);

        MonitoredService persisted = (await client.GetFromJsonAsync<MonitoredService>($"/services/{created.Id}", TestJson.Options))!;
        Assert.Equal(ServiceStatus.Up, persisted.CurrentStatus);
        Assert.NotNull(persisted.LastCheckedAt);
        Assert.Equal(record.CheckedAtUtc, persisted.LastCheckedAt.Value.UtcDateTime);

        List<CheckRecord> history = (await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{created.Id}/history", TestJson.Options))!;
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

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        app.Probe.Result = new ProbeResult(ServiceStatus.Down, 503, 31, "HTTP 503");
        HttpResponseMessage secondCheck = await client.PostAsync($"/services/{created.Id}/check", null);
        secondCheck.EnsureSuccessStatusCode();

        List<CheckRecord> firstPage = (await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{created.Id}/history?take=1", TestJson.Options))!;
        CheckRecord newest = Assert.Single(firstPage);
        Assert.Equal(ServiceStatus.Down, newest.Status);

        List<CheckRecord> secondPage = (await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{created.Id}/history?skip=1&take=1", TestJson.Options))!;
        Assert.Single(secondPage);
    }

    [Fact]
    public async Task UnknownResultReplacesEarlierStatusAndIsSavedInHistory()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService created = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", "https://example.com", IsEnabled: false));
        HttpResponseMessage firstCheck = await client.PostAsync($"/services/{created.Id}/check", null);
        firstCheck.EnsureSuccessStatusCode();

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        app.Probe.Result = new ProbeResult(ServiceStatus.Unknown, null, 3, "Destination address blocked by policy.");
        HttpResponseMessage secondCheck = await client.PostAsync($"/services/{created.Id}/check", null);
        secondCheck.EnsureSuccessStatusCode();

        MonitoredService persisted = (await client.GetFromJsonAsync<MonitoredService>($"/services/{created.Id}", TestJson.Options))!;
        Assert.Equal(ServiceStatus.Unknown, persisted.CurrentStatus);
        List<CheckRecord> history = (await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{created.Id}/history", TestJson.Options))!;
        Assert.Equal(2, history.Count);
        Assert.Equal(ServiceStatus.Unknown, history[0].Status);
        Assert.Equal("Destination address blocked by policy.", history[0].FailureReason);
        Assert.Equal(ServiceStatus.Up, history[1].Status);
    }

    // The existing rows hold this text, so the application must keep writing exactly the same.
    [Fact]
    public async Task StatusIsStoredAsUppercaseText()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService created = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", "https://example.com", IsEnabled: false));
        HttpResponseMessage check = await client.PostAsync($"/services/{created.Id}/check", null);
        check.EnsureSuccessStatusCode();

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        List<string> currentStatus = await db.Database.SqlQuery<string>($"SELECT CurrentStatus AS Value FROM MonitoredServices").ToListAsync();
        List<string> recordStatus = await db.Database.SqlQuery<string>($"SELECT Status AS Value FROM CheckRecords").ToListAsync();

        Assert.Equal("UP", Assert.Single(currentStatus));
        Assert.Equal("UP", Assert.Single(recordStatus));
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
        List<CheckRecord> history = (await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{service.Id}/history", TestJson.Options))!;
        Assert.Empty(history);

        // A new check can only succeed if the cancelled check released its lock.
        app.Probe.Check = null;
        HttpResponseMessage nextCheck = await client.PostAsync($"/services/{service.Id}/check", null);
        nextCheck.EnsureSuccessStatusCode();
    }

    // Two random ids almost never collide, so two services would not prove anything here. The old
    // design had 64 shared locks, so 65 services were guaranteed to make at least two share one.
    [Fact]
    public async Task ChecksOfManyDifferentServicesRunAtTheSameTime()
    {
        const int serviceCount = 65;
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        TaskCompletionSource allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int startedCount = 0;

        app.Probe.Check = async cancellationToken =>
        {
            if (Interlocked.Increment(ref startedCount) == serviceCount)
            {
                allStarted.SetResult();
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return app.Probe.Result;
        };

        List<Task> checks = new List<Task>();
        for (int i = 0; i < serviceCount; i++)
        {
            MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration($"Service {i}", "https://example.com"));
            checks.Add(CheckInOwnScopeAsync(app, service.Id, cancellation.Token));
        }

        await Task.WhenAny(allStarted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();

        int rejectedCount = 0;
        foreach (Task check in checks)
        {
            try
            {
                await check;
            }
            catch (CheckInProgressException)
            {
                rejectedCount++;
            }
            catch (OperationCanceledException)
            {
                // Expected: this is how the hanging checks are ended.
            }
        }

        Assert.Equal(0, rejectedCount);
        Assert.Equal(serviceCount, startedCount);
    }

    [Fact]
    public async Task UpdateDoesNotWaitForCheckOfAnotherService()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService checkedService = await app.RegisterServiceAsync(client, new ServiceConfiguration("Checked", "https://example.com"));
        MonitoredService otherService = await app.RegisterServiceAsync(client, new ServiceConfiguration("Other", "https://example.com"));
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        TaskCompletionSource started = HangProbeUntilCancelled(app.Probe);

        Task check = CheckInOwnScopeAsync(app, checkedService.Id, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ServiceConfiguration changed = new ServiceConfiguration("Changed", "https://example.com");
        HttpResponseMessage update = await client.PutAsJsonAsync($"/services/{otherService.Id}", changed).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    [Fact]
    public async Task UpdateWaitsForRunningCheckOfSameService()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", "https://example.com"));
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        TaskCompletionSource started = HangProbeUntilCancelled(app.Probe);

        Task check = CheckInOwnScopeAsync(app, service.Id, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ServiceConfiguration changed = new ServiceConfiguration("Changed", "https://example.com");
        Task<HttpResponseMessage> update = client.PutAsJsonAsync($"/services/{service.Id}", changed);

        // A short wait cannot prove "never", but an update that ignored the lock finishes well within it.
        await Task.WhenAny(update, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.False(update.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);

        HttpResponseMessage response = await update.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeleteWaitsForRunningCheckOfSameService()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", "https://example.com"));
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        TaskCompletionSource started = HangProbeUntilCancelled(app.Probe);

        Task check = CheckInOwnScopeAsync(app, service.Id, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<HttpResponseMessage> delete = client.DeleteAsync($"/services/{service.Id}");

        // A short wait cannot prove "never", but a delete that ignored the lock finishes well within it.
        await Task.WhenAny(delete, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.False(delete.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);

        HttpResponseMessage response = await delete.WaitAsync(TimeSpan.FromSeconds(5));
        response.EnsureSuccessStatusCode();
    }

    // A lock is taken before the code knows whether the service exists. Requests with random ids
    // must not leave anything behind, or anyone could make the memory grow.
    [Fact]
    public async Task CheckOfMissingServiceLeavesNoLockBehind()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();

        HttpResponseMessage check = await client.PostAsync($"/services/{Guid.NewGuid()}/check", null);

        Assert.Equal(HttpStatusCode.NotFound, check.StatusCode);
        ServiceLocks locks = app.Services.GetRequiredService<ServiceLocks>();
        Assert.Equal(0, locks.ActiveCount);
    }

    // Every check needs its own scope, because a DbContext must not be used by two checks at once.
    private static async Task CheckInOwnScopeAsync(TestApp app, Guid id, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        MonitoredServiceService services = scope.ServiceProvider.GetRequiredService<MonitoredServiceService>();
        await services.CheckAsync(id, ct: cancellationToken);
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
