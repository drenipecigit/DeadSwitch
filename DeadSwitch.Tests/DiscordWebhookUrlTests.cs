using DeadSwitch.Infrastructure.Notifications;

namespace DeadSwitch.Tests;

public class DiscordWebhookUrlTests
{
    [Theory]
    [InlineData("https://discord.com/api/webhooks/123456789/abcDEF_123-xyz")]
    [InlineData("https://discordapp.com/api/webhooks/123456789/abcDEF_123-xyz")]
    public void RealFormIsAllowed(string url)
    {
        Assert.True(DiscordWebhookUrl.IsAllowed(url));
    }

    // Every row here would change where the message goes, or what Discord does with it.
    [Theory]
    // Scheme
    [InlineData("http://discord.com/api/webhooks/123/token")]
    [InlineData("ftp://discord.com/api/webhooks/123/token")]
    // Another host
    [InlineData("https://example.com/api/webhooks/123/token")]
    [InlineData("https://discord.com.example.com/api/webhooks/123/token")]
    [InlineData("https://example.com/discord.com/api/webhooks/123/token")]
    [InlineData("https://notdiscord.com/api/webhooks/123/token")]
    [InlineData("https://ptb.discord.com/api/webhooks/123/token")]
    [InlineData("https://canary.discord.com/api/webhooks/123/token")]
    [InlineData("https://discord.gg/api/webhooks/123/token")]
    [InlineData("https://127.0.0.1/api/webhooks/123/token")]
    [InlineData("https://discord.com./api/webhooks/123/token")]
    // A host hidden behind other parts of the address
    [InlineData("https://discord.com@example.com/api/webhooks/123/token")]
    [InlineData("https://user:password@discord.com/api/webhooks/123/token")]
    [InlineData("https://example.com\\@discord.com/api/webhooks/123/token")]
    [InlineData("https://example.com#@discord.com/api/webhooks/123/token")]
    // Port
    [InlineData("https://discord.com:8443/api/webhooks/123/token")]
    [InlineData("https://discord.com:443/api/webhooks/123/token")]
    // Forms that Uri would rewrite without saying so
    [InlineData("HTTPS://discord.com/api/webhooks/123/token")]
    [InlineData("https://DISCORD.com/api/webhooks/123/token")]
    [InlineData("https://discord.com/other/../api/webhooks/123/token")]
    [InlineData("https://discord.com/api/webhooks/../../other/123/token")]
    [InlineData("https://discord.com\\api\\webhooks\\123\\token")]
    [InlineData(" https://discord.com/api/webhooks/123/token")]
    [InlineData("https://discord.com/api/webhooks/123/token ")]
    [InlineData("https://discord.com/api/webhooks/123/to ken")]
    [InlineData("https://discord.com/api/webhooks/123/token\n")]
    [InlineData("https://discord.com/api/webhooks/123/tok\ten")]
    // Characters outside ASCII: the fullwidth d, and a soft hyphen
    [InlineData("https://ｄiscord.com/api/webhooks/123/token")]
    [InlineData("https://discord.com/api/webhooks/123/tok­en")]
    // Path
    [InlineData("https://discord.com/")]
    [InlineData("https://discord.com/api/webhooks")]
    [InlineData("https://discord.com/api/webhooks/")]
    [InlineData("https://discord.com/api/webhooks/123")]
    [InlineData("https://discord.com/api/webhooks/123/")]
    [InlineData("https://discord.com/api/webhooks//token")]
    [InlineData("https://discord.com/api/webhooks/abc/token")]
    [InlineData("https://discord.com/api/webhooks/123/token/")]
    [InlineData("https://discord.com/api/webhooks/123/token/slack")]
    [InlineData("https://discord.com/api/webhooks/123/token/github")]
    [InlineData("https://discord.com/api/webhooks/123/to%2Fken")]
    [InlineData("https://discord.com/api/webhooks/123%2F456/token")]
    [InlineData("https://discord.com/api/v10/webhooks/123/token")]
    [InlineData("https://discord.com/API/webhooks/123/token")]
    [InlineData("https://discord.com/other/api/webhooks/123/token")]
    // Query and fragment
    [InlineData("https://discord.com/api/webhooks/123/token?wait=true")]
    [InlineData("https://discord.com/api/webhooks/123/token?")]
    [InlineData("https://discord.com/api/webhooks/123/token#part")]
    // Not an address at all
    [InlineData("discord.com/api/webhooks/123/token")]
    [InlineData("/api/webhooks/123/token")]
    [InlineData("not a url")]
    [InlineData(" ")]
    public void EverythingElseIsRejected(string url)
    {
        Assert.False(DiscordWebhookUrl.IsAllowed(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoAddressIsValidAndMeansNotificationsAreOff(string? url)
    {
        DiscordOptions options = new DiscordOptions();
        options.WebhookUrl = url;

        Assert.True(DiscordOptions.HasValidWebhookUrl(options));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("http://example.com/x")]
    public void AnyOtherTextInTheOptionsIsChecked(string url)
    {
        DiscordOptions options = new DiscordOptions();
        options.WebhookUrl = url;

        Assert.False(DiscordOptions.HasValidWebhookUrl(options));
    }

    [Fact]
    public void TheFixedMessageHoldsNoPartOfAnAddress()
    {
        Assert.DoesNotContain("://", DiscordOptions.InvalidWebhookUrlMessage);
    }
}
