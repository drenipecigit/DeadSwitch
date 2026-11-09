using System.Text;
using System.Text.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeadSwitch.Tests;

// Every test sends through new HttpClient(stub) or a registered stub, so nothing can reach the network.
public class SecurityWarningDiscordTests
{
    private const string Prefix = "DeadSwitch security warning: ";

    [Fact]
    public async Task HeaderMessageNamesTheServiceAndTheHeadersAndNothingElse()
    {
        SecurityWarning warning = CreateHeaderWarning("Shop");

        string content = await SendAndReadContentAsync(warning);

        Assert.Equal(Prefix + "`Shop` no longer sends: Content-Security-Policy, X-Frame-Options.", content);
    }

    [Fact]
    public async Task RejectedMessageIsAFixedText()
    {
        SecurityWarning warning = new SecurityWarning(Guid.NewGuid(), "Shop", SecurityWarningKind.CertificateRejected, Array.Empty<string>(), null);

        string content = await SendAndReadContentAsync(warning);

        Assert.Equal(Prefix + "the TLS certificate of `Shop` was rejected.", content);
    }

    [Theory]
    [InlineData(14, "14 days")]
    [InlineData(7, "7 days")]
    [InlineData(1, "1 day")]
    public async Task ExpiryMessageGivesOnlyTheThreshold(int threshold, string expectedText)
    {
        SecurityWarning warning = new SecurityWarning(Guid.NewGuid(), "Shop", SecurityWarningKind.CertificateExpiringSoon, Array.Empty<string>(), threshold);

        string content = await SendAndReadContentAsync(warning);

        Assert.Equal(Prefix + "the TLS certificate of `Shop` has at most " + expectedText + " left.", content);
    }

    [Fact]
    public async Task SendsAPostToTheWebhookAndAsksDiscordToConfirm()
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        DiscordNotifier notifier = CreateNotifier(stub, TestWebhook.Url);

        await notifier.NotifySecurityWarningAsync(CreateHeaderWarning("Shop"));

        Assert.Equal(1, stub.Calls);
        Assert.Equal(HttpMethod.Post, stub.LastMethod);
        Assert.Equal(TestWebhook.Url + "?wait=true", stub.LastUrl!.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WithoutAnAddressNothingIsSent(string? webhookUrl)
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        DiscordNotifier notifier = CreateNotifier(stub, webhookUrl);

        await notifier.NotifySecurityWarningAsync(CreateHeaderWarning("Shop"));

        Assert.Equal(0, stub.Calls);
    }

    // Proves what is sent. Whether Discord obeys it can only be seen in a manual run.
    [Theory]
    [InlineData("@everyone")]
    [InlineData("@here")]
    [InlineData("<@123456789>")]
    [InlineData("<@&123456789>")]
    public async Task MentionInANameTurnsOffEveryPing(string name)
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        DiscordNotifier notifier = CreateNotifier(stub, TestWebhook.Url);

        await notifier.NotifySecurityWarningAsync(CreateHeaderWarning(name));

        using JsonDocument document = JsonDocument.Parse(stub.LastBody);
        JsonElement parse = document.RootElement.GetProperty("allowed_mentions").GetProperty("parse");
        Assert.Equal(0, parse.GetArrayLength());
        string content = document.RootElement.GetProperty("content").GetString()!;
        Assert.Contains("`" + name + "`", content);
    }

    [Theory]
    [InlineData("[click](https://example.org)")]
    [InlineData("https://example.org")]
    public async Task LinkInANameStaysInsideTheCodeFormat(string name)
    {
        string content = await SendAndReadContentAsync(CreateHeaderWarning(name));

        Assert.Equal(Prefix + "`" + name + "` no longer sends: Content-Security-Policy, X-Frame-Options.", content);
    }

    [Fact]
    public async Task BacktickInANameCannotEndTheCodeFormat()
    {
        string content = await SendAndReadContentAsync(CreateHeaderWarning("a`[click](https://example.org)`b"));

        Assert.Contains("`a'[click](https://example.org)'b`", content);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\tb")]
    public async Task ControlCharacterInANameBecomesASpace(string name)
    {
        string content = await SendAndReadContentAsync(CreateHeaderWarning(name));

        Assert.Contains("`a b`", content);
    }

    // Written as numbers, so that the source file holds no invisible characters of its own:
    // line separator, paragraph separator, zero width space, right-to-left override, byte order mark.
    [Theory]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x200B)]
    [InlineData(0x202E)]
    [InlineData(0xFEFF)]
    public async Task LineSeparatorOrInvisibleCharacterInANameBecomesASpace(int characterCode)
    {
        string name = "a" + (char)characterCode + "b";

        string content = await SendAndReadContentAsync(CreateHeaderWarning(name));

        Assert.Contains("`a b`", content);
    }

    // The same cleaning is used by the status messages, so the change must reach them too.
    [Theory]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x200B)]
    [InlineData(0x202E)]
    [InlineData(0xFEFF)]
    public async Task StatusMessageAlsoReplacesLineSeparatorsAndInvisibleCharacters(int characterCode)
    {
        string name = "a" + (char)characterCode + "b";
        StubDiscordHandler stub = new StubDiscordHandler();
        DiscordNotifier notifier = CreateNotifier(stub, TestWebhook.Url);
        StatusChange change = new StatusChange(Guid.NewGuid(), name, "https://example.com", ServiceStatus.Up, ServiceStatus.Down, DateTimeOffset.UnixEpoch, null);

        await notifier.NotifyStatusChangedAsync(change);

        using JsonDocument document = JsonDocument.Parse(stub.LastBody);
        Assert.Contains("`a b`", document.RootElement.GetProperty("content").GetString()!);
    }

    [Fact]
    public async Task OrdinaryLettersAndEmojiAreLeftAlone()
    {
        string content = await SendAndReadContentAsync(CreateHeaderWarning("Åre 😀"));

        Assert.Contains("`Åre 😀`", content);
    }

    [Fact]
    public async Task NameIsCutAtItsLimit()
    {
        string content = await SendAndReadContentAsync(CreateHeaderWarning(new string('a', 101)));

        Assert.Contains("`" + new string('a', 100) + "...`", content);
    }

    [Fact]
    public async Task LongestPossibleMessageStaysWellWithinDiscordsLimit()
    {
        SecurityWarning warning = new SecurityWarning(Guid.NewGuid(), new string('å', 10000), SecurityWarningKind.HeadersDisappeared, TestSecurityHeaders.AllNames, null);

        string content = await SendAndReadContentAsync(warning);

        Assert.True(content.Length <= 2000, "Too many characters: " + content.Length);
        Assert.True(Encoding.UTF8.GetByteCount(content) <= 2000, "Too many bytes.");
    }

    [Fact]
    public async Task FailureGivesTheSameFixedExceptionAsAStatusMessage()
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        stub.ExceptionToThrow = new HttpRequestException("Could not send to " + TestWebhook.Url);
        DiscordNotifier notifier = CreateNotifier(stub, TestWebhook.Url);

        Task send = notifier.NotifySecurityWarningAsync(CreateHeaderWarning("Shop"));

        NotificationFailedException exception = await Assert.ThrowsAsync<NotificationFailedException>(() => send);
        Assert.Equal(DiscordNotifier.UnreachableMessage, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task HangingDiscordGivesAFixedFailure()
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        stub.HangUntilCancelled = true;
        DiscordNotifier notifier = CreateNotifier(stub, TestWebhook.Url);
        notifier.SendTimeout = TimeSpan.FromMilliseconds(50);

        Task send = notifier.NotifySecurityWarningAsync(CreateHeaderWarning("Shop"));

        // The limit is only there so that a missing timeout gives a red test instead of one that hangs.
        Task sendWithLimit = send.WaitAsync(TimeSpan.FromSeconds(10));
        NotificationFailedException exception = await Assert.ThrowsAsync<NotificationFailedException>(() => sendWithLimit);
        Assert.Equal(DiscordNotifier.TimedOutMessage, exception.Message);
    }

    // The whole chain: the real registration, the real rules and the real message, with a stub as Discord.
    [Fact]
    public async Task RealNotifierSendsAWarningWithoutTheUrlOrAnyDate()
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        await using TestApp app = new TestApp("Development", TestWebhook.Url, stub);
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        app.AssertDiscordClientUsesTheStub();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("@everyone", "https://example.com/secret-path"));
        DateTime notAfter = app.Clock.GetUtcNow().UtcDateTime.AddDays(90);

        app.Probe.Result = TestProbeResults.Response(200, TestProbeResults.HeadersAssessed(""), TestProbeResults.CertificateValid(notAfter));
        HttpResponseMessage first = await client.PostAsync($"/services/{service.Id}/check", null);
        first.EnsureSuccessStatusCode();
        Assert.Equal(0, stub.Calls);

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        app.Probe.Result = TestProbeResults.Response(200, TestProbeResults.HeadersAssessed(TestSecurityHeaders.FrameOptions), TestProbeResults.CertificateValid(notAfter));
        HttpResponseMessage second = await client.PostAsync($"/services/{service.Id}/check", null);
        second.EnsureSuccessStatusCode();

        Assert.Equal(1, stub.Calls);
        using JsonDocument document = JsonDocument.Parse(stub.LastBody);
        string content = document.RootElement.GetProperty("content").GetString()!;
        Assert.Equal(Prefix + "`@everyone` no longer sends: X-Frame-Options.", content);
        Assert.Equal(0, document.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
    }

    private static SecurityWarning CreateHeaderWarning(string name)
    {
        string[] headers = { TestSecurityHeaders.ContentSecurityPolicy, TestSecurityHeaders.FrameOptions };
        return new SecurityWarning(Guid.NewGuid(), name, SecurityWarningKind.HeadersDisappeared, headers, null);
    }

    private static async Task<string> SendAndReadContentAsync(SecurityWarning warning)
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        DiscordNotifier notifier = CreateNotifier(stub, TestWebhook.Url);

        await notifier.NotifySecurityWarningAsync(warning);

        using JsonDocument document = JsonDocument.Parse(stub.LastBody);
        return document.RootElement.GetProperty("content").GetString()!;
    }

    private static DiscordNotifier CreateNotifier(StubDiscordHandler stub, string? webhookUrl)
    {
        DiscordOptions options = new DiscordOptions();
        options.WebhookUrl = webhookUrl;
        return new DiscordNotifier(new HttpClient(stub), Options.Create(options), NullLogger<DiscordNotifier>.Instance);
    }
}
