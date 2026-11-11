using System.Net.Http.Json;
using DeadSwitch.Api.Monitoring;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

// A single failed check must not give a DOWN alarm. Only the second failed check in a row does.
public class FailedChecksInARowTests
{
    private const string FirstUrl = "https://example.com";
    private const string SecondUrl = "https://example.org";

    private static readonly ProbeResult Up = new ProbeResult(ServiceStatus.Up, 200, 12, null);

    [Fact]
    public async Task SingleTimeoutAfterUpGivesNoNotification()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());

        Assert.Empty(app.Notifier.GetChanges());
    }

    [Fact]
    public async Task SingleTimeoutThenUpGivesNoNotification()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());
        await PostCheckAsync(app, client, service.Id, Up);

        Assert.Empty(app.Notifier.GetChanges());
    }

    // An Up between two timeouts ends the row, so the timeouts are not in a row.
    [Fact]
    public async Task UpBetweenTwoTimeoutsStartsTheCountOver()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());
        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());

        Assert.Empty(app.Notifier.GetChanges());
    }

    [Fact]
    public async Task TwoTimeoutsInARowNotifyOnce()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());

        StatusChange change = Assert.Single(app.Notifier.GetChanges());
        Assert.Equal(ServiceStatus.Up, change.PreviousStatus);
        Assert.Equal(ServiceStatus.Down, change.NewStatus);
        Assert.Equal(TestProbeResults.TimedOutReason, change.FailureReason);
    }

    // Every kind of DOWN counts in the same way, not only timeouts.
    [Theory]
    [InlineData("timeout")]
    [InlineData("connection failure")]
    [InlineData("client error")]
    [InlineData("server error")]
    public async Task EveryKindOfDownNeedsTwoFailedChecksInARow(string kind)
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, FailureOfKind(kind));
        Assert.Empty(app.Notifier.GetChanges());

        await PostCheckAsync(app, client, service.Id, FailureOfKind(kind));

        StatusChange change = Assert.Single(app.Notifier.GetChanges());
        Assert.Equal(ServiceStatus.Down, change.NewStatus);
    }

    [Fact]
    public async Task DifferentKindsOfDownCountTogether()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());
        await PostCheckAsync(app, client, service.Id, FailureOfKind("server error"));

        StatusChange change = Assert.Single(app.Notifier.GetChanges());
        Assert.Equal("HTTP 500", change.FailureReason);
    }

    [Fact]
    public async Task FirstUpAfterConfirmedDownNotifiesAtOnce()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());
        await PostCheckAsync(app, client, service.Id, Up);

        List<StatusChange> changes = app.Notifier.GetChanges();
        Assert.Equal(2, changes.Count);
        Assert.Equal(ServiceStatus.Down, changes[1].PreviousStatus);
        Assert.Equal(ServiceStatus.Up, changes[1].NewStatus);
    }

    [Fact]
    public async Task UnknownAfterOneFailureDoesNotNotify()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Blocked());

        Assert.Empty(app.Notifier.GetChanges());
    }

    // Unknown means that no request was sent. It says nothing about the service, so it does not end the row.
    [Fact]
    public async Task UnknownBetweenTwoFailuresNeitherCountsNorResets()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Blocked());
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());

        StatusChange change = Assert.Single(app.Notifier.GetChanges());
        Assert.Equal(ServiceStatus.Up, change.PreviousStatus);
        Assert.Equal(ServiceStatus.Down, change.NewStatus);
    }

    [Fact]
    public async Task NewServiceNeedsTwoFailedChecksInARow()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());
        Assert.Empty(app.Notifier.GetChanges());

        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());

        StatusChange change = Assert.Single(app.Notifier.GetChanges());
        Assert.Null(change.PreviousStatus);
        Assert.Equal(ServiceStatus.Down, change.NewStatus);
    }

    // A failed check of the old address says nothing about the new one.
    [Fact]
    public async Task NewUrlStartsTheCountOver()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());
        await ChangeUrlAsync(client, service.Id, SecondUrl);
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());

        Assert.Empty(app.Notifier.GetChanges());
        Assert.Equal(1, await ReadFailedChecksInARowAsync(app, service.Id));
    }

    // Nothing is known about the new address, so its DOWN alarm has no old status, not the UP of the old one.
    [Fact]
    public async Task TwoFailuresOnANewUrlAfterOneOnTheOldUrlGiveOneDownWithoutAnOldStatus()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());
        await ChangeUrlAsync(client, service.Id, SecondUrl);
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());
        Assert.Empty(app.Notifier.GetChanges());

        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());

        StatusChange change = Assert.Single(app.Notifier.GetChanges());
        Assert.Null(change.PreviousStatus);
        Assert.Equal(ServiceStatus.Down, change.NewStatus);
        Assert.Equal(SecondUrl, change.ServiceUrl);
    }

    // Only the alarm waits. The history and the current status still show what the check saw.
    [Fact]
    public async Task SingleFailureStillSavesDownInHistoryAndCurrentStatus()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);

        // A later time, so the history has a clear newest check.
        app.Clock.Advance(TimeSpan.FromSeconds(60));
        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());

        MonitoredService? saved = await client.GetFromJsonAsync<MonitoredService>($"/services/{service.Id}", TestJson.Options);
        Assert.Equal(ServiceStatus.Down, saved!.CurrentStatus);
        Assert.Equal(ServiceStatus.Up, saved.LastKnownStatus);

        List<CheckRecord>? history = await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{service.Id}/history", TestJson.Options);
        Assert.Equal(2, history!.Count);
        Assert.Equal(ServiceStatus.Down, history[0].Status);
        Assert.Equal(TestProbeResults.TimedOutReason, history[0].FailureReason);
    }

    [Fact]
    public async Task CountIsSavedForEveryCheck()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);
        Assert.Equal(0, await ReadFailedChecksInARowAsync(app, service.Id));

        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());
        Assert.Equal(1, await ReadFailedChecksInARowAsync(app, service.Id));

        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());
        Assert.Equal(2, await ReadFailedChecksInARowAsync(app, service.Id));

        await PostCheckAsync(app, client, service.Id, TestProbeResults.Blocked());
        Assert.Equal(2, await ReadFailedChecksInARowAsync(app, service.Id));

        await PostCheckAsync(app, client, service.Id, Up);
        Assert.Equal(0, await ReadFailedChecksInARowAsync(app, service.Id));
    }

    [Fact]
    public async Task WorkerAndManualCheckShareTheCount()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl, IntervalSeconds: 10));
        using MonitoringWorker worker = app.CreateWorker();

        await PostCheckAsync(app, client, service.Id, Up);
        app.Clock.Advance(TimeSpan.FromSeconds(11));
        app.Probe.Result = TestProbeResults.Timeout();
        await worker.RunPassAsync(CancellationToken.None);
        Assert.Equal(2, app.Probe.Calls);
        Assert.Empty(app.Notifier.GetChanges());

        await PostCheckAsync(app, client, service.Id, TestProbeResults.Timeout());

        StatusChange change = Assert.Single(app.Notifier.GetChanges());
        Assert.Equal(ServiceStatus.Down, change.NewStatus);
    }

    // The second app uses the same database and the first one is stopped, which is what a restart is.
    [Fact]
    public async Task CountSurvivesARestart()
    {
        string databaseName = "restart-" + Guid.NewGuid();

        // Started before the first app is stopped, or the in-memory database would be deleted.
        await using TestApp afterRestart = new TestApp("Development", databaseName);
        MonitoredService service;

        // Not "await using": the first app must be stopped in the middle of the test. The finally
        // stops it also when an assertion fails.
        TestApp beforeRestart = new TestApp("Development", databaseName);
        try
        {
            await beforeRestart.MigrateAsync();
            using HttpClient clientBeforeRestart = beforeRestart.CreateClient();
            service = await RegisterAsync(beforeRestart, clientBeforeRestart);
            await PostCheckAsync(beforeRestart, clientBeforeRestart, service.Id, Up);
            await PostCheckAsync(beforeRestart, clientBeforeRestart, service.Id, TestProbeResults.Timeout());
            Assert.Empty(beforeRestart.Notifier.GetChanges());
        }
        finally
        {
            await beforeRestart.DisposeAsync();
        }

        using HttpClient clientAfterRestart = afterRestart.CreateClient();
        await PostCheckAsync(afterRestart, clientAfterRestart, service.Id, TestProbeResults.Timeout());

        StatusChange change = Assert.Single(afterRestart.Notifier.GetChanges());
        Assert.Equal(ServiceStatus.Up, change.PreviousStatus);
        Assert.Equal(ServiceStatus.Down, change.NewStatus);
    }

    private static ProbeResult FailureOfKind(string kind)
    {
        if (kind == "timeout")
        {
            return TestProbeResults.Timeout();
        }

        if (kind == "connection failure")
        {
            return TestProbeResults.ConnectionFailure();
        }

        if (kind == "client error")
        {
            return TestProbeResults.Response(404, null, null);
        }

        if (kind == "server error")
        {
            return TestProbeResults.Response(500, null, null);
        }

        throw new ArgumentException("Unknown kind of failure: " + kind);
    }

    private static async Task<TestApp> CreateAppAsync()
    {
        TestApp app = new TestApp();
        await app.MigrateAsync();
        return app;
    }

    private static Task<MonitoredService> RegisterAsync(TestApp app, HttpClient client)
    {
        return app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
    }

    private static async Task PostCheckAsync(TestApp app, HttpClient client, Guid id, ProbeResult result)
    {
        app.Probe.Result = result;
        HttpResponseMessage response = await client.PostAsync($"/services/{id}/check", null);
        response.EnsureSuccessStatusCode();
    }

    private static async Task ChangeUrlAsync(HttpClient client, Guid id, string url)
    {
        ServiceConfiguration configuration = new ServiceConfiguration("Shop", url);
        HttpResponseMessage response = await client.PutAsJsonAsync($"/services/{id}", configuration);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<int> ReadFailedChecksInARowAsync(TestApp app, Guid id)
    {
        MonitoredService service = await app.ReadSavedServiceAsync(id);
        return service.FailedChecksInARow;
    }
}
