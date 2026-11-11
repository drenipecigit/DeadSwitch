using System.Net;
using System.Net.Http.Json;
using DeadSwitch.Api.Monitoring;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using Microsoft.Extensions.Logging;

namespace DeadSwitch.Tests;

public class NotificationBehaviourTests
{
    private const string FirstUrl = "https://example.com";
    private const string SecondUrl = "https://example.org";

    private static readonly ProbeResult Up = new ProbeResult(ServiceStatus.Up, 200, 12, null);
    private static readonly ProbeResult Down = new ProbeResult(ServiceStatus.Down, 500, 12, "HTTP 500");
    private static readonly ProbeResult Unknown = new ProbeResult(ServiceStatus.Unknown, null, 12, "URL rejected by destination policy.");

    [Fact]
    public async Task ManualCheckFromUpToDownNotifiesWithEveryField()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        await ConfirmDownAsync(app, client, service.Id);

        StatusChange change = Assert.Single(app.Notifier.GetChanges());
        Assert.Equal(service.Id, change.ServiceId);
        Assert.Equal("Shop", change.ServiceName);
        Assert.Equal(FirstUrl, change.ServiceUrl);
        Assert.Equal(ServiceStatus.Up, change.PreviousStatus);
        Assert.Equal(ServiceStatus.Down, change.NewStatus);
        Assert.Equal(app.Clock.GetUtcNow(), change.CheckedAt);
        Assert.Equal("HTTP 500", change.FailureReason);
    }

    // A link or an image tag can send a GET without the owner noticing, so a GET must change nothing.
    // The service has one failed check, so a check here would confirm DOWN and send an alarm.
    [Fact]
    public async Task GetOnTheCheckRouteChangesNothing()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);
        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, Down);
        MonitoredService before = await app.ReadSavedServiceAsync(service.Id);

        // A later time, so a check would also change the time of the last check.
        app.Clock.Advance(TimeSpan.FromSeconds(60));
        app.Probe.Result = Down;
        HttpResponseMessage response = await client.GetAsync($"/services/{service.Id}/check");

        Assert.Equal(2, app.Probe.Calls);
        Assert.Empty(app.Notifier.GetChanges());

        List<CheckRecord>? history = await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{service.Id}/history", TestJson.Options);
        Assert.Equal(2, history!.Count);

        MonitoredService after = await app.ReadSavedServiceAsync(service.Id);
        Assert.Equal(before.FailedChecksInARow, after.FailedChecksInARow);
        Assert.Equal(before.CurrentStatus, after.CurrentStatus);
        Assert.Equal(before.LastKnownStatus, after.LastKnownStatus);
        Assert.Equal(before.LastCheckedAt, after.LastCheckedAt);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task WorkerNotifiesLikeTheManualRoute()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl, IntervalSeconds: 10));
        using MonitoringWorker worker = app.CreateWorker();

        app.Probe.Result = Up;
        await worker.RunPassAsync(CancellationToken.None);
        Assert.Empty(app.Notifier.GetChanges());

        app.Clock.Advance(TimeSpan.FromSeconds(11));
        app.Probe.Result = Down;
        await worker.RunPassAsync(CancellationToken.None);
        Assert.Empty(app.Notifier.GetChanges());

        app.Clock.Advance(TimeSpan.FromSeconds(11));
        await worker.RunPassAsync(CancellationToken.None);

        StatusChange change = Assert.Single(app.Notifier.GetChanges());
        Assert.Equal(ServiceStatus.Up, change.PreviousStatus);
        Assert.Equal(ServiceStatus.Down, change.NewStatus);
    }

    [Fact]
    public async Task WorkerDoesNotNotifyOnEveryCheck()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl, IntervalSeconds: 10));
        using MonitoringWorker worker = app.CreateWorker();

        app.Probe.Result = Down;
        for (int pass = 0; pass < 5; pass++)
        {
            await worker.RunPassAsync(CancellationToken.None);
            app.Clock.Advance(TimeSpan.FromSeconds(11));
        }

        Assert.Equal(5, app.Probe.Calls);
        Assert.Single(app.Notifier.GetChanges());
    }

    [Fact]
    public async Task FromDownToUpNotifies()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await ConfirmDownAsync(app, client, service.Id);
        await PostCheckAsync(app, client, service.Id, Up);

        List<StatusChange> changes = app.Notifier.GetChanges();
        Assert.Equal(2, changes.Count);
        Assert.Equal(ServiceStatus.Down, changes[1].PreviousStatus);
        Assert.Equal(ServiceStatus.Up, changes[1].NewStatus);
        Assert.Null(changes[1].FailureReason);
    }

    [Fact]
    public async Task RepeatedUpDoesNotNotify()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, Up);

        Assert.Empty(app.Notifier.GetChanges());
    }

    // A misspelled URL that is down from the start would otherwise never give an alarm.
    [Fact]
    public async Task FirstCheckThatIsDownNotifiesOnce()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await ConfirmDownAsync(app, client, service.Id);
        await PostCheckAsync(app, client, service.Id, Down);

        StatusChange change = Assert.Single(app.Notifier.GetChanges());
        Assert.Null(change.PreviousStatus);
        Assert.Equal(ServiceStatus.Down, change.NewStatus);
    }

    [Fact]
    public async Task FirstCheckThatIsUpDoesNotNotify()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);

        Assert.Empty(app.Notifier.GetChanges());
    }

    [Fact]
    public async Task FirstCheckThatIsUnknownDoesNotNotify()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Unknown);

        Assert.Empty(app.Notifier.GetChanges());
    }

    [Fact]
    public async Task NewFaultyUrlGivesADownNotificationWithoutAnOldStatus()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);
        await PostCheckAsync(app, client, service.Id, Up);

        await ChangeUrlAsync(client, service.Id, SecondUrl);
        await ConfirmDownAsync(app, client, service.Id);

        StatusChange change = Assert.Single(app.Notifier.GetChanges());
        Assert.Null(change.PreviousStatus);
        Assert.Equal(ServiceStatus.Down, change.NewStatus);
        Assert.Equal(SecondUrl, change.ServiceUrl);
    }

    [Fact]
    public async Task NewUrlForgetsTheOldStatus()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);
        await ConfirmDownAsync(app, client, service.Id);
        int notificationsBefore = app.Notifier.GetChanges().Count;

        await ChangeUrlAsync(client, service.Id, SecondUrl);
        await PostCheckAsync(app, client, service.Id, Up);

        Assert.Equal(notificationsBefore, app.Notifier.GetChanges().Count);
    }

    [Fact]
    public async Task NewNameDoesNotForgetTheOldStatus()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);
        await PostCheckAsync(app, client, service.Id, Up);

        ServiceConfiguration renamed = new ServiceConfiguration("Renamed", FirstUrl);
        HttpResponseMessage response = await client.PutAsJsonAsync($"/services/{service.Id}", renamed);
        response.EnsureSuccessStatusCode();
        await PostCheckAsync(app, client, service.Id, Up);
        Assert.Empty(app.Notifier.GetChanges());

        await ConfirmDownAsync(app, client, service.Id);
        StatusChange change = Assert.Single(app.Notifier.GetChanges());
        Assert.Equal(ServiceStatus.Up, change.PreviousStatus);
        Assert.Equal("Renamed", change.ServiceName);
    }

    [Fact]
    public async Task UpThenUnknownThenDownNotifiesWithUpAsThePreviousStatus()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, Unknown);
        Assert.Empty(app.Notifier.GetChanges());

        await ConfirmDownAsync(app, client, service.Id);

        StatusChange change = Assert.Single(app.Notifier.GetChanges());
        Assert.Equal(ServiceStatus.Up, change.PreviousStatus);
        Assert.Equal(ServiceStatus.Down, change.NewStatus);
    }

    [Fact]
    public async Task UpThenUnknownThenUpDoesNotNotify()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        await PostCheckAsync(app, client, service.Id, Unknown);
        await PostCheckAsync(app, client, service.Id, Up);

        Assert.Empty(app.Notifier.GetChanges());
    }

    [Fact]
    public async Task DownThenUnknownThenDownDoesNotNotifyAgain()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await ConfirmDownAsync(app, client, service.Id);
        await PostCheckAsync(app, client, service.Id, Unknown);
        await PostCheckAsync(app, client, service.Id, Down);

        Assert.Single(app.Notifier.GetChanges());
    }

    [Fact]
    public async Task DownThenUnknownThenUpNotifies()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await ConfirmDownAsync(app, client, service.Id);
        await PostCheckAsync(app, client, service.Id, Unknown);
        await PostCheckAsync(app, client, service.Id, Up);

        List<StatusChange> changes = app.Notifier.GetChanges();
        Assert.Equal(2, changes.Count);
        Assert.Equal(ServiceStatus.Down, changes[1].PreviousStatus);
        Assert.Equal(ServiceStatus.Up, changes[1].NewStatus);
    }

    [Fact]
    public async Task UnknownOnANewServiceThenDownNotifies()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Unknown);
        await ConfirmDownAsync(app, client, service.Id);

        StatusChange change = Assert.Single(app.Notifier.GetChanges());
        Assert.Null(change.PreviousStatus);
    }

    [Fact]
    public async Task ChangeToUnknownIsLoggedButNotSent()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        await PostCheckAsync(app, client, service.Id, Up);
        Assert.False(app.Logs.Contains(LogLevel.Warning, "changed to UNKNOWN"));

        await PostCheckAsync(app, client, service.Id, Unknown);

        Assert.True(app.Logs.Contains(LogLevel.Warning, "changed to UNKNOWN"));
        Assert.Empty(app.Notifier.GetChanges());
    }

    // This route saves nothing, so there is no status to compare with.
    [Fact]
    public async Task CheckOfAnUnsavedUrlNeverNotifies()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();

        app.Probe.Result = Down;
        HttpResponseMessage response = await client.GetAsync("/check?url=https://example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(app.Notifier.GetChanges());
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

    // DOWN is only confirmed, and a message only sent, at the second failed check in a row.
    private static async Task ConfirmDownAsync(TestApp app, HttpClient client, Guid id)
    {
        await PostCheckAsync(app, client, id, Down);
        await PostCheckAsync(app, client, id, Down);
    }

    private static async Task ChangeUrlAsync(HttpClient client, Guid id, string url)
    {
        ServiceConfiguration configuration = new ServiceConfiguration("Shop", url);
        HttpResponseMessage response = await client.PutAsJsonAsync($"/services/{id}", configuration);
        response.EnsureSuccessStatusCode();
    }
}
