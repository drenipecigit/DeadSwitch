namespace DeadSwitch.Infrastructure.Notifications;

// Filled from the "Discord" section of the configuration. The address is a secret, so it belongs in
// user secrets or an environment variable, and never in appsettings.json or git.
public sealed class DiscordOptions
{
    // A constant that never holds the address: this text is shown when the application refuses to start.
    public const string InvalidWebhookUrlMessage = "Discord:WebhookUrl must be an https address on discord.com or discordapp.com under /api/webhooks/.";

    public string? WebhookUrl { get; set; }

    // No address at all is valid and means that notifications are turned off.
    public static bool HasValidWebhookUrl(DiscordOptions options)
    {
        if (string.IsNullOrEmpty(options.WebhookUrl))
        {
            return true;
        }

        return DiscordWebhookUrl.IsAllowed(options.WebhookUrl);
    }
}
