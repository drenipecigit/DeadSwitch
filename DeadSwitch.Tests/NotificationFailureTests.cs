using System.Net;
using System.Net.Http.Json;
using DeadSwitch.Api.Monitoring;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using Microsoft.Extensions.Logging;

namespace DeadSwitch.Tests;

public class NotificationFailureTests
{
    private const string ExpectedFailureText = "The channel did not answer.";
    private const string SecretText = "text-that-must-never-be-logged";

    private static readonly ProbeResult Down = new ProbeResult(ServiceStatus.Down, 500, 12, "HTTP 500");

    [Fact]
    public async Task ExpectedFailureKeepsTheCheckAndIsLoggedAsAWarning()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", "https://example.com"));
        app.Notifier.ExceptionToThrow = new NotificationFailedException(ExpectedFailureText);

        HttpResponseMessage response = await CheckTwiceAsDownAsync(app, client, service.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertBothChecksAreSavedAsync(client, service.Id);
        Assert.True(app.Logs.Contains(LogLevel.Warning, ExpectedFailureText));
    }

    // Only the type may reach the log, because the text of an unexpected exception can hold anything.
    [Fact]
    public async Task UnexpectedFailureKeepsTheCheckAndLogsOnlyTheType()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", "https://example.com"));
        app.Notifier.ExceptionToThrow = new InvalidOperationException(SecretText);

        HttpResponseMessage response = await CheckTwiceAsDownAsync(app, client, service.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertBothChecksAreSavedAsync(client, service.Id);
        Assert.True(app.Logs.Contains(LogLevel.Error, nameof(InvalidOperationException)));
        app.Logs.AssertNothingContains(SecretText);
    }

    [Fact]
    public async Task WorkerGoesOnToTheNextServiceWhenTheNotificationFails()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService first = await app.RegisterServiceAsync(client, new ServiceConfiguration("First", "https://example.com"));
        MonitoredService second = await app.RegisterServiceAsync(client, new ServiceConfiguration("Second", "https://example.org"));
        app.Notifier.ExceptionToThrow = new InvalidOperationException(SecretText);
        using MonitoringWorker worker = app.CreateWorker();

        // Two passes, because a DOWN message is only sent at the second failed check in a row.
        app.Probe.Result = Down;
        await worker.RunPassAsync(CancellationToken.None);
        app.Clock.Advance(TimeSpan.FromSeconds(61));
        await worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(4, app.Probe.Calls);
        Assert.True(app.Logs.Contains(LogLevel.Error, nameof(InvalidOperationException)));
        await AssertBothChecksAreSavedAsync(client, first.Id);
        await AssertBothChecksAreSavedAsync(client, second.Id);
    }

    // Returns the answer to the second check, which is the one that tries to notify.
    private static async Task<HttpResponseMessage> CheckTwiceAsDownAsync(TestApp app, HttpClient client, Guid id)
    {
        app.Probe.Result = Down;
        HttpResponseMessage firstCheck = await client.PostAsync($"/services/{id}/check", null);
        firstCheck.EnsureSuccessStatusCode();

        return await client.PostAsync($"/services/{id}/check", null);
    }

    private static async Task AssertBothChecksAreSavedAsync(HttpClient client, Guid id)
    {
        List<CheckRecord>? history = await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{id}/history", TestJson.Options);
        Assert.Equal(2, history!.Count);
        Assert.Equal(ServiceStatus.Down, history[0].Status);
        Assert.Equal(ServiceStatus.Down, history[1].Status);

        MonitoredService? service = await client.GetFromJsonAsync<MonitoredService>($"/services/{id}", TestJson.Options);
        Assert.Equal(ServiceStatus.Down, service!.LastKnownStatus);
    }
}
