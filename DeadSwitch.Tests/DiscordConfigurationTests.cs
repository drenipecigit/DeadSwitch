using System.Net;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeadSwitch.Tests;

public class DiscordConfigurationTests
{
    // Development reads user secrets, so without its own setting this host would see a real address.
    [Fact]
    public async Task OrdinaryTestsUseTheFakeNotifierAndNoAddress()
    {
        await using TestApp app = new TestApp();
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();

        INotifier notifier = scope.ServiceProvider.GetRequiredService<INotifier>();
        IOptions<DiscordOptions> options = scope.ServiceProvider.GetRequiredService<IOptions<DiscordOptions>>();

        Assert.Same(app.Notifier, notifier);
        Assert.Equal("", options.Value.WebhookUrl);
    }

    [Theory]
    [InlineData("http://discord.com/api/webhooks/123/SECRETPART")]
    [InlineData("https://example.com/api/webhooks/123/SECRETPART")]
    [InlineData("https://discord.com/other/123/SECRETPART")]
    [InlineData("https://discord.com/api/webhooks/123/SECRETPART/slack")]
    [InlineData("SECRETPART")]
    public async Task InvalidAddressStopsTheApplicationWithAFixedText(string webhookUrl)
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        await using TestApp app = new TestApp("Development", webhookUrl, stub);
        app.WaitForProgramBeforeStart = true;

        // Reading Services is what starts the application.
        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(() => app.Services);

        Assert.Equal(DiscordOptions.InvalidWebhookUrlMessage, exception.Message);
        Assert.DoesNotContain("SECRETPART", exception.ToString());
        app.Logs.AssertNothingContains("SECRETPART");
        Assert.Equal(0, stub.Calls);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task WithoutAnAddressTheApplicationWorksAsBefore(string environmentName)
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        await using TestApp app = new TestApp(environmentName, "", stub);
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        app.AssertDiscordClientUsesTheStub();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", "https://example.com"));

        // Two failed checks, because only the second one in a row tries to send a DOWN message.
        app.Probe.Result = new ProbeResult(ServiceStatus.Down, 500, 12, "HTTP 500");
        HttpResponseMessage firstCheck = await client.PostAsync($"/services/{service.Id}/check", null);
        firstCheck.EnsureSuccessStatusCode();
        HttpResponseMessage response = await client.PostAsync($"/services/{service.Id}/check", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, stub.Calls);
        Assert.True(app.Logs.Contains(LogLevel.Information, DiscordOptions.NotificationsOffMessage));
        Assert.False(app.Logs.Contains(LogLevel.Warning, "Notification failed"));
        Assert.False(app.Logs.Contains(LogLevel.Error, "Notification failed"));
    }

    [Fact]
    public async Task ValidAddressIsReportedAtStartupWithoutBeingShown()
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        await using TestApp app = new TestApp("Development", TestWebhook.Url, stub);
        using HttpClient client = app.CreateClient();

        Assert.True(app.Logs.Contains(LogLevel.Information, DiscordOptions.NotificationsOnMessage));
        app.Logs.AssertNothingContains(TestWebhook.Token);
    }

    // Reads the files as they are built. This does not stop an address in another file.
    [Fact]
    public void SettingsFilesDoNotMentionTheWebhook()
    {
        string[] files = Directory.GetFiles(AppContext.BaseDirectory, "appsettings*.json");
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("webhook", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("discord", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
