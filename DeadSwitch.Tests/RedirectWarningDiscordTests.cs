using System.Text.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeadSwitch.Tests;

// Every test sends through a stub, so nothing can reach Discord. These tests prove what is sent. How
// Discord shows it can only be seen in a manual run.
public class RedirectWarningDiscordTests
{
    private const string Prefix = "DeadSwitch security warning: ";
    private const string HostileLocation = "https://user-marker:password-marker@other.example/path-marker?query-marker#fragment-marker";

    private static readonly string[] Markers =
    {
        "user-marker",
        "password-marker",
        "path-marker",
        "query-marker",
        "fragment-marker",
    };

    [Fact]
    public async Task AnotherHostIsWrittenAsCode()
    {
        string content = await SendAndReadContentAsync(CreateWarning("Shop", SecurityWarningKind.RedirectToOtherHost, "other.example"));

        Assert.Equal(Prefix + "`Shop` now redirects to another domain: `other.example`.", content);
    }

    [Fact]
    public async Task AnInternalTargetIsAFixedText()
    {
        string content = await SendAndReadContentAsync(CreateWarning("Shop", SecurityWarningKind.RedirectToInternalTarget, null));

        Assert.Equal(Prefix + "`Shop` now redirects to a private or internal address.", content);
    }

    [Fact]
    public async Task AnUnreadableTargetIsAFixedText()
    {
        string content = await SendAndReadContentAsync(CreateWarning("Shop", SecurityWarningKind.RedirectUnreadable, null));

        Assert.Equal(Prefix + "`Shop` now sends a redirect whose target cannot be read.", content);
    }

    // The parser never lets these through. This proves the second layer, if that one ever fails.
    [Theory]
    [InlineData("@everyone")]
    [InlineData("@here")]
    [InlineData("<@123456789>")]
    public async Task AMentionInAHostTurnsOffEveryPing(string host)
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        DiscordNotifier notifier = CreateNotifier(stub);

        await notifier.NotifySecurityWarningAsync(CreateWarning("Shop", SecurityWarningKind.RedirectToOtherHost, host));

        using JsonDocument document = JsonDocument.Parse(stub.LastBody);
        Assert.Equal(0, document.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
        Assert.Contains("`" + host + "`", document.RootElement.GetProperty("content").GetString()!);
    }

    [Fact]
    public async Task ABacktickInAHostCannotEndTheCodeFormat()
    {
        string content = await SendAndReadContentAsync(CreateWarning("Shop", SecurityWarningKind.RedirectToOtherHost, "a`[click](https://example.org)`b"));

        Assert.Contains("`a'[click](https://example.org)'b`", content);
    }

    [Fact]
    public async Task ALineBreakInAHostBecomesASpace()
    {
        string content = await SendAndReadContentAsync(CreateWarning("Shop", SecurityWarningKind.RedirectToOtherHost, "a\nb"));

        Assert.Contains("`a b`", content);
    }

    [Fact]
    public async Task AHostIsCutAtItsLimit()
    {
        string content = await SendAndReadContentAsync(CreateWarning("Shop", SecurityWarningKind.RedirectToOtherHost, new string('a', 254)));

        Assert.Contains("`" + new string('a', 253) + "...`", content);
    }

    [Fact]
    public async Task TheLongestMessageStaysWithinDiscordsLimit()
    {
        string content = await SendAndReadContentAsync(CreateWarning(new string('a', 10000), SecurityWarningKind.RedirectToOtherHost, new string('b', 10000)));

        Assert.True(content.Length <= 2000, "Too many characters: " + content.Length);
    }

    // The whole chain after the probe: the real parser, the real rules, the real message, a stub as Discord.
    [Fact]
    public async Task NothingButTheHostReachesDiscordTheApiOrTheLog()
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        await using TestApp app = new TestApp("Development", TestWebhook.Url, stub);
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        app.AssertDiscordClientUsesTheStub();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", "https://monitored.test/"));

        app.Probe.Result = TestProbeResults.NoRedirect();
        HttpResponseMessage first = await client.PostAsync($"/services/{service.Id}/check", null);
        first.EnsureSuccessStatusCode();
        Assert.Equal(0, stub.Calls);

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        string[] values = { HostileLocation };
        RedirectTargetCheck? target = RedirectTargetParser.Parse(new Uri("https://monitored.test/"), values);
        app.Probe.Result = new ProbeResult(ServiceStatus.Up, 302, 12, null, null, null, target);
        HttpResponseMessage second = await client.PostAsync($"/services/{service.Id}/check", null);
        second.EnsureSuccessStatusCode();

        Assert.Equal(1, stub.Calls);
        using JsonDocument document = JsonDocument.Parse(stub.LastBody);
        Assert.Equal(Prefix + "`Shop` now redirects to another domain: `other.example`.", document.RootElement.GetProperty("content").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());

        string checkAnswer = await second.Content.ReadAsStringAsync();
        string history = await client.GetStringAsync($"/services/{service.Id}/history");
        string overview = await client.GetStringAsync("/services/overview");

        foreach (string marker in Markers)
        {
            Assert.DoesNotContain(marker, stub.LastBody);
            Assert.DoesNotContain(marker, checkAnswer);
            Assert.DoesNotContain(marker, history);
            Assert.DoesNotContain(marker, overview);
            app.Logs.AssertNothingContains(marker);
        }
    }

    private static SecurityWarning CreateWarning(string name, SecurityWarningKind kind, string? host)
    {
        return new SecurityWarning(Guid.NewGuid(), name, kind, Array.Empty<string>(), null, host);
    }

    private static async Task<string> SendAndReadContentAsync(SecurityWarning warning)
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        DiscordNotifier notifier = CreateNotifier(stub);

        await notifier.NotifySecurityWarningAsync(warning);

        using JsonDocument document = JsonDocument.Parse(stub.LastBody);
        return document.RootElement.GetProperty("content").GetString()!;
    }

    private static DiscordNotifier CreateNotifier(StubDiscordHandler stub)
    {
        DiscordOptions options = new DiscordOptions();
        options.WebhookUrl = TestWebhook.Url;
        return new DiscordNotifier(new HttpClient(stub), Options.Create(options), NullLogger<DiscordNotifier>.Instance);
    }
}
