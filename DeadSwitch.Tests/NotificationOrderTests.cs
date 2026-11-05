using System.Net;
using System.Net.Http.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace DeadSwitch.Tests;

public class NotificationOrderTests
{
    private static readonly ProbeResult Up = new ProbeResult(ServiceStatus.Up, 200, 12, null);
    private static readonly ProbeResult Down = new ProbeResult(ServiceStatus.Down, 500, 12, "HTTP 500");

    [Fact]
    public async Task CheckIsAlreadySavedWhenTheNotificationIsSent()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", "https://example.com"));
        int savedChecksWhenNotified = 0;

        // Reads from a scope of its own, so it sees what is really in the database.
        async Task CountSavedChecks()
        {
            await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
            MonitoredServiceService services = scope.ServiceProvider.GetRequiredService<MonitoredServiceService>();
            List<CheckRecord> history = await services.GetHistoryAsync(service.Id, 0, 10);
            savedChecksWhenNotified = history.Count;
        }

        app.Notifier.RunWhenNotified = CountSavedChecks;
        app.Probe.Result = Down;
        HttpResponseMessage response = await client.PostAsync($"/services/{service.Id}/check", null);
        response.EnsureSuccessStatusCode();

        Assert.Single(app.Notifier.GetChanges());
        Assert.Equal(1, savedChecksWhenNotified);
    }

    // A slow channel must not hold the lock, or it would make updates wait and manual checks get 409.
    [Fact]
    public async Task LockIsReleasedWhenTheNotificationIsSent()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", "https://example.com"));
        ServiceLocks locks = app.Services.GetRequiredService<ServiceLocks>();
        int locksWhenNotified = -1;

        Task CountLocks()
        {
            locksWhenNotified = locks.ActiveCount;
            return Task.CompletedTask;
        }

        app.Notifier.RunWhenNotified = CountLocks;
        app.Probe.Result = Down;
        HttpResponseMessage response = await client.PostAsync($"/services/{service.Id}/check", null);
        response.EnsureSuccessStatusCode();

        Assert.Single(app.Notifier.GetChanges());
        Assert.Equal(0, locksWhenNotified);
    }

    [Fact]
    public async Task CheckThatCouldNotBeSavedDoesNotNotify()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", "https://example.com"));

        app.RepositoryControl.FailSaveCheck = true;
        app.Probe.Result = Down;
        HttpResponseMessage response = await client.PostAsync($"/services/{service.Id}/check", null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(app.Notifier.GetChanges());
    }

    [Fact]
    public async Task CancelledCheckDoesNotNotify()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", "https://example.com"));
        app.Probe.Result = Up;
        HttpResponseMessage firstCheck = await client.PostAsync($"/services/{service.Id}/check", null);
        firstCheck.EnsureSuccessStatusCode();

        using CancellationTokenSource cancellation = new CancellationTokenSource();

        // Cancels during the probe, which is exactly when a client that disconnects would do it.
        Task<ProbeResult> CancelAndAnswerDown(CancellationToken token)
        {
            cancellation.Cancel();
            return Task.FromResult(Down);
        }

        app.Probe.Check = CancelAndAnswerDown;
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        MonitoredServiceService services = scope.ServiceProvider.GetRequiredService<MonitoredServiceService>();

        Task<CheckRecord?> check = services.CheckAsync(service.Id, ct: cancellation.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() => check);
        Assert.Empty(app.Notifier.GetChanges());
        MonitoredService? found = await client.GetFromJsonAsync<MonitoredService>($"/services/{service.Id}", TestJson.Options);
        Assert.Equal(ServiceStatus.Up, found!.LastKnownStatus);
    }
}
