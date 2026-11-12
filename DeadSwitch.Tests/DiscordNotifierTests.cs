using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeadSwitch.Tests;

// Every test sends through new HttpClient(stub), so nothing here can reach the network.
public class DiscordNotifierTests
{
    private const string ExampleUrl = "https://example.com/health";

    [Fact]
    public async Task SendsAPostToTheWebhookAndAsksDiscordToConfirm()
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        DiscordNotifier notifier = CreateNotifier(stub, TestWebhook.Url);

        await notifier.NotifyStatusChangedAsync(CreateChange("Shop", ExampleUrl));

        Assert.Equal(1, stub.Calls);
        Assert.Equal(HttpMethod.Post, stub.LastMethod);
        Assert.Equal(TestWebhook.Url + "?wait=true", stub.LastUrl!.AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task AnyDiscordSuccessIsASuccess(HttpStatusCode status)
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        stub.StatusToReturn = status;
        DiscordNotifier notifier = CreateNotifier(stub, TestWebhook.Url);

        await notifier.NotifyStatusChangedAsync(CreateChange("Shop", ExampleUrl));

        Assert.Equal(1, stub.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WithoutAnAddressNothingIsSent(string? webhookUrl)
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        DiscordNotifier notifier = CreateNotifier(stub, webhookUrl);

        await notifier.NotifyStatusChangedAsync(CreateChange("Shop", ExampleUrl));

        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task MessageSaysWhatHappened()
    {
        string content = await SendAndReadContentAsync(CreateChange("Shop", ExampleUrl));

        Assert.Equal("DeadSwitch: `Shop` is DOWN (" + FailedChecksText() + "). Reason: HTTP 500. Checked <t:1793862953:f>.", content);
    }

    // Shown for every DOWN alarm, also for a new service, because an alarm now always means that
    // several checks in a row have failed.
    [Theory]
    [InlineData(ServiceStatus.Up)]
    [InlineData(null)]
    public async Task DownMessageSaysHowManyChecksFailedInARow(ServiceStatus? previousStatus)
    {
        StatusChange change = new StatusChange(Guid.NewGuid(), "Shop", ExampleUrl, previousStatus, ServiceStatus.Down, CreateTime(), "HTTP 500");

        string content = await SendAndReadContentAsync(change);

        Assert.Contains("is DOWN (" + FailedChecksText() + ").", content);
    }

    [Fact]
    public async Task DownMessageNeverSaysFirstCheck()
    {
        StatusChange change = new StatusChange(Guid.NewGuid(), "Shop", ExampleUrl, null, ServiceStatus.Down, CreateTime(), "HTTP 500");

        string content = await SendAndReadContentAsync(change);

        Assert.DoesNotContain("first check", content);
    }

    [Fact]
    public async Task UpMessageStillSaysWasDown()
    {
        StatusChange change = new StatusChange(Guid.NewGuid(), "Shop", ExampleUrl, ServiceStatus.Down, ServiceStatus.Up, CreateTime(), null);

        string content = await SendAndReadContentAsync(change);

        Assert.Contains("is UP (was DOWN).", content);
        Assert.DoesNotContain("failed checks", content);
    }

    // The rule never sends this. If it happens anyway, nothing is made up about an earlier status.
    [Fact]
    public async Task UpMessageWithoutAPreviousStatusHasNoBrackets()
    {
        StatusChange change = new StatusChange(Guid.NewGuid(), "Shop", ExampleUrl, null, ServiceStatus.Up, CreateTime(), null);

        string content = await SendAndReadContentAsync(change);

        Assert.Equal("DeadSwitch: `Shop` is UP. Checked <t:1793862953:f>.", content);
    }

    [Fact]
    public async Task MessageWithoutAReasonLeavesItOut()
    {
        StatusChange change = new StatusChange(Guid.NewGuid(), "Shop", ExampleUrl, ServiceStatus.Down, ServiceStatus.Up, CreateTime(), null);

        string content = await SendAndReadContentAsync(change);

        Assert.Equal("DeadSwitch: `Shop` is UP (was DOWN). Checked <t:1793862953:f>.", content);
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

        await notifier.NotifyStatusChangedAsync(CreateChange(name, ExampleUrl));

        using JsonDocument document = JsonDocument.Parse(stub.LastBody);
        JsonElement allowedMentions = document.RootElement.GetProperty("allowed_mentions");
        JsonElement parse = allowedMentions.GetProperty("parse");
        Assert.Equal(JsonValueKind.Array, parse.ValueKind);
        Assert.Equal(0, parse.GetArrayLength());

        // Discord's "users" and "roles" lists would allow pings again, so nothing else may be here.
        int fieldCount = 0;
        foreach (JsonProperty field in allowedMentions.EnumerateObject())
        {
            fieldCount++;
        }

        Assert.Equal(1, fieldCount);
    }

    [Fact]
    public async Task BacktickInANameCannotEndTheCodeFormat()
    {
        string content = await SendAndReadContentAsync(CreateChange("a`[click](https://example.org)`b", ExampleUrl));

        Assert.Contains("`a'[click](https://example.org)'b`", content);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\tb")]
    public async Task ControlCharacterInANameBecomesASpace(string name)
    {
        string content = await SendAndReadContentAsync(CreateChange(name, ExampleUrl));

        Assert.Contains("`a b`", content);
    }

    // Discord is a third party and the dashboard already shows the address, so no part of it is sent:
    // not the host, not the path, and not a query that can hold a token.
    [Theory]
    [InlineData("https://monitored.example/health-path")]
    [InlineData("https://monitored.example/health-path?token=secret-marker")]
    public async Task UrlIsNeverSentToDiscord(string url)
    {
        string content = await SendAndReadContentAsync(CreateChange("Shop", url));

        AssertHoldsNoPartOfTheUrl(content);
    }

    [Fact]
    public async Task LongestUrlIsNeverSentToDiscord()
    {
        string start = "https://monitored.example/health-path/";
        string url = start + new string('b', 2048 - start.Length);

        string content = await SendAndReadContentAsync(CreateChange("Shop", url));

        AssertHoldsNoPartOfTheUrl(content);
        Assert.DoesNotContain("bbb", content);
    }

    // Seconds since 1970 in Discord's own tag, which every reader sees in their own time zone. The
    // time in these tests is 2026-11-05T07:15:53.290Z, and the part of a second is dropped.
    [Fact]
    public async Task TimeIsWrittenInDiscordsOwnFormat()
    {
        string content = await SendAndReadContentAsync(CreateChange("Shop", ExampleUrl));

        Assert.EndsWith(" Checked <t:1793862953:f>.", content);
        Assert.DoesNotContain("2026", content);
        Assert.DoesNotContain("Time:", content);
    }

    // A name that looks like a time tag must be shown as the letters it is, and not as a time.
    [Fact]
    public async Task ATimeTagInANameStaysInsideTheCodeFormat()
    {
        string content = await SendAndReadContentAsync(CreateChange("<t:0:f>", ExampleUrl));

        Assert.StartsWith("DeadSwitch: `<t:0:f>` is DOWN", content);
    }

    [Fact]
    public async Task MessageIsOneLine()
    {
        string content = await SendAndReadContentAsync(CreateChange("Shop", ExampleUrl));

        Assert.DoesNotContain("\n", content);
        Assert.DoesNotContain("\r", content);
    }

    [Fact]
    public async Task LongestPossibleValuesStayWellWithinDiscordsLimit()
    {
        string longName = new string('å', 10000);
        string longUrl = "https://example.com/" + new string('b', 10000);

        string content = await SendAndReadContentAsync(CreateChange(longName, longUrl));

        // Discord allows 2000 characters without saying how they are counted, so bytes are tested too.
        Assert.True(content.Length <= 2000, "Too many characters: " + content.Length);
        Assert.True(Encoding.UTF8.GetByteCount(content) <= 2000, "Too many bytes.");
    }

    [Fact]
    public async Task NameIsCutAtItsLimit()
    {
        string content = await SendAndReadContentAsync(CreateChange(new string('a', 101), ExampleUrl));

        Assert.Contains("`" + new string('a', 100) + "...`", content);
    }

    [Fact]
    public async Task NameAtItsLimitIsNotCut()
    {
        string content = await SendAndReadContentAsync(CreateChange(new string('a', 100), ExampleUrl));

        Assert.Contains("`" + new string('a', 100) + "`", content);
    }

    // An emoji is two chars in C#. Half of one would reach Discord as a broken character.
    [Fact]
    public async Task EmojiAtTheLimitIsNotCutInHalf()
    {
        string name = new string('a', 99) + "😀" + "b";

        string content = await SendAndReadContentAsync(CreateChange(name, ExampleUrl));

        Assert.Contains("`" + new string('a', 99) + "...`", content);
        Assert.DoesNotContain("�", content);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public async Task AnswerThatIsNotASuccessGivesAFixedFailureAfterOneRequest(HttpStatusCode status)
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        stub.StatusToReturn = status;
        DiscordNotifier notifier = CreateNotifier(stub, TestWebhook.Url);

        Task send = notifier.NotifyStatusChangedAsync(CreateChange("Shop", ExampleUrl));

        NotificationFailedException exception = await Assert.ThrowsAsync<NotificationFailedException>(() => send);
        Assert.Equal("Discord answered with status " + (int)status + ".", exception.Message);
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task NetworkFailureGivesAFixedFailureWithoutTheAddress()
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        stub.ExceptionToThrow = new HttpRequestException("Could not send to " + TestWebhook.Url);
        DiscordNotifier notifier = CreateNotifier(stub, TestWebhook.Url);

        Task send = notifier.NotifyStatusChangedAsync(CreateChange("Shop", ExampleUrl));

        NotificationFailedException exception = await Assert.ThrowsAsync<NotificationFailedException>(() => send);
        Assert.Equal(DiscordNotifier.UnreachableMessage, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(TestWebhook.Token, exception.ToString());
    }

    [Fact]
    public async Task HangingDiscordGivesAFixedFailure()
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        stub.HangUntilCancelled = true;
        DiscordNotifier notifier = CreateNotifier(stub, TestWebhook.Url);
        notifier.SendTimeout = TimeSpan.FromMilliseconds(50);

        Task send = notifier.NotifyStatusChangedAsync(CreateChange("Shop", ExampleUrl));

        // The limit is only there so that a missing timeout gives a red test instead of one that hangs.
        Task sendWithLimit = send.WaitAsync(TimeSpan.FromSeconds(10));
        NotificationFailedException exception = await Assert.ThrowsAsync<NotificationFailedException>(() => sendWithLimit);
        Assert.Equal(DiscordNotifier.TimedOutMessage, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void DefaultTimeoutIsFiveSeconds()
    {
        DiscordNotifier notifier = CreateNotifier(new StubDiscordHandler(), TestWebhook.Url);

        Assert.Equal(TimeSpan.FromSeconds(5), notifier.SendTimeout);
    }

    private static void AssertHoldsNoPartOfTheUrl(string content)
    {
        Assert.DoesNotContain("monitored.example", content);
        Assert.DoesNotContain("health-path", content);
        Assert.DoesNotContain("secret-marker", content);
        Assert.DoesNotContain("http", content);
        Assert.DoesNotContain("URL", content);
    }

    private static async Task<string> SendAndReadContentAsync(StatusChange change)
    {
        StubDiscordHandler stub = new StubDiscordHandler();
        DiscordNotifier notifier = CreateNotifier(stub, TestWebhook.Url);

        await notifier.NotifyStatusChangedAsync(change);

        using JsonDocument document = JsonDocument.Parse(stub.LastBody);
        return document.RootElement.GetProperty("content").GetString()!;
    }

    private static DiscordNotifier CreateNotifier(StubDiscordHandler stub, string? webhookUrl)
    {
        DiscordOptions options = new DiscordOptions();
        options.WebhookUrl = webhookUrl;
        return new DiscordNotifier(new HttpClient(stub), Options.Create(options), NullLogger<DiscordNotifier>.Instance);
    }

    private static StatusChange CreateChange(string name, string url)
    {
        return new StatusChange(Guid.NewGuid(), name, url, ServiceStatus.Up, ServiceStatus.Down, CreateTime(), "HTTP 500");
    }

    // Built from the constant, so these tests follow along if the number of checks is changed.
    private static string FailedChecksText()
    {
        return DownConfirmationRule.FailedChecksBeforeDown.ToString(CultureInfo.InvariantCulture) + " failed checks in a row";
    }

    private static DateTimeOffset CreateTime()
    {
        return new DateTimeOffset(2026, 11, 5, 7, 15, 53, 290, TimeSpan.Zero);
    }
}
