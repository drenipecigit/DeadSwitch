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

        app.Probe.Result = Down;
        HttpResponseMessage response = await client.PostAsync($"/services/{service.Id}/check", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertCheckIsSavedAsync(client, service.Id);
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

        app.Probe.Result = Down;
        HttpResponseMessage response = await client.PostAsync($"/services/{service.Id}/check", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertCheckIsSavedAsync(client, service.Id);
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

        app.Probe.Result = Down;
        await worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(2, app.Probe.Calls);
        await AssertCheckIsSavedAsync(client, first.Id);
        await AssertCheckIsSavedAsync(client, second.Id);
    }

    private static async Task AssertCheckIsSavedAsync(HttpClient client, Guid id)
    {
        List<CheckRecord>? history = await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{id}/history", TestJson.Options);
        CheckRecord record = Assert.Single(history!);
        Assert.Equal(ServiceStatus.Down, record.Status);

        MonitoredService? service = await client.GetFromJsonAsync<MonitoredService>($"/services/{id}", TestJson.Options);
        Assert.Equal(ServiceStatus.Down, service!.LastKnownStatus);
    }
}
