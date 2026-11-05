using System.Net;
using System.Text.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Notifications;
using Microsoft.Extensions.Logging;

namespace DeadSwitch.Tests;

// Runs the real notifier through the real registration in Program.cs, with everything logged at the
// lowest level. Only the handler at the bottom is a stub, so nothing here reaches the network.
public class WebhookSecrecyTests
{
    private const string WebhookPath = "api/webhooks";

    private static readonly ProbeResult Down = new ProbeResult(ServiceStatus.Down, 500, 12, "HTTP 500");

    [Fact]
    public async Task SuccessfulSendLogsNoAddress()
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        await using TestApp app = await CreateAppAsync(stub);
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await CheckNewServiceAsync(app, client);

        Assert.Equal(1, stub.Calls);
        Assert.Equal(TestWebhook.Url + "?wait=true", stub.LastUrl!.AbsoluteUri);

        // The control: the capture really sees this send, so a clean log means something.
        Assert.True(app.Logs.Contains(LogLevel.Information, "Discord notification sent"));
        await AssertAddressIsNotShownAsync(app, response);
    }

    [Fact]
    public async Task DiscordErrorLogsNoAddress()
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        stub.StatusToReturn = HttpStatusCode.InternalServerError;
        await using TestApp app = await CreateAppAsync(stub);
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await CheckNewServiceAsync(app, client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(app.Logs.Contains(LogLevel.Warning, "Discord answered with status 500."));
        await AssertAddressIsNotShownAsync(app, response);
    }

    [Fact]
    public async Task NetworkFailureThatNamesTheAddressLogsNoAddress()
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        stub.ExceptionToThrow = new HttpRequestException("Could not send to " + TestWebhook.Url);
        await using TestApp app = await CreateAppAsync(stub);
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await CheckNewServiceAsync(app, client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(app.Logs.Contains(LogLevel.Warning, DiscordNotifier.UnreachableMessage));
        await AssertAddressIsNotShownAsync(app, response);
    }

    [Fact]
    public async Task UnexpectedFailureThatNamesTheAddressLogsNoAddress()
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        stub.ExceptionToThrow = new InvalidOperationException("Could not send to " + TestWebhook.Url);
        await using TestApp app = await CreateAppAsync(stub);
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await CheckNewServiceAsync(app, client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(app.Logs.Contains(LogLevel.Error, nameof(InvalidOperationException)));
        await AssertAddressIsNotShownAsync(app, response);
    }

    // The same as the test in DiscordNotifierTests, but through the whole application.
    [Fact]
    public async Task EveryoneInANameGivesNoPingThroughTheWholeApplication()
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        await using TestApp app = await CreateAppAsync(stub);
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("@everyone", "https://example.com"));

        app.Probe.Result = Down;
        HttpResponseMessage response = await client.PostAsync($"/services/{service.Id}/check", null);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(stub.LastBody);
        Assert.Contains("@everyone", document.RootElement.GetProperty("content").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
    }

    private static async Task<TestApp> CreateAppAsync(StubDiscordHandler stub)
    {
        TestApp app = new TestApp("Development", TestWebhook.Url, stub);
        await app.MigrateAsync();
        app.AssertDiscordClientUsesTheStub();
        return app;
    }

    private static async Task<HttpResponseMessage> CheckNewServiceAsync(TestApp app, HttpClient client)
    {
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", "https://example.com"));
        app.Probe.Result = Down;
        return await client.PostAsync($"/services/{service.Id}/check", null);
    }

    private static async Task AssertAddressIsNotShownAsync(TestApp app, HttpResponseMessage response)
    {
        app.Logs.AssertNothingContains(TestWebhook.Token);
        app.Logs.AssertNothingContains(WebhookPath);

        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(TestWebhook.Token, body);
        Assert.DoesNotContain(WebhookPath, body);
    }
}
