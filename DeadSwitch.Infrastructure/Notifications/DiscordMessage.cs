using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;

namespace DeadSwitch.Infrastructure.Notifications;

// The JSON body of a Discord webhook. The name and the URL come from the user, so they are treated as
// untrusted text here: they must not be able to ping anyone or change how the message looks.
internal sealed class DiscordMessage
{
    // Discord allows 2000 characters in a message. A URL may be 2048 long, so it must be cut. These
    // limits leave a wide margin, because Discord does not say whether it counts characters or bytes.
    private const int MaxNameLength = 100;
    private const int MaxUrlLength = 300;

    // The longest a host name can be.
    private const int MaxHostLength = 253;

    [JsonPropertyName("content")]
    public string Content { get; }

    // Always present and always empty: this is what turns off every ping.
    [JsonPropertyName("allowed_mentions")]
    public DiscordAllowedMentions AllowedMentions { get; } = new DiscordAllowedMentions();

    private DiscordMessage(string content)
    {
        Content = content;
    }

    public static DiscordMessage Create(StatusChange change)
    {
        string name = FormatAsCode(change.ServiceName, MaxNameLength);
        string url = FormatAsCode(RemoveEverythingAfterThePath(change.ServiceUrl), MaxUrlLength);
        string status = ToText(change.NewStatus);
        string time = change.CheckedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        string previous;
        if (change.PreviousStatus == null)
        {
            previous = "first check";
        }
        else
        {
            previous = "was " + ToText(change.PreviousStatus.Value);
        }

        string content = "DeadSwitch: " + name + " is " + status + " (" + previous + "). URL: " + url + ".";

        // FailureReason only ever holds fixed texts from the probe, so it needs no cleaning.
        if (change.FailureReason != null)
        {
            content += " Reason: " + change.FailureReason;
            if (!content.EndsWith('.'))
            {
                content += ".";
            }
        }

        content += " Time: " + time;

        return new DiscordMessage(content);
    }

    // Security: only the name and a redirect's host come from outside this class's fixed texts, and both
    // go through the same cleaning as above. The host has already passed the allowlist in
    // RedirectTargetParser, so this is the second layer. It is written without a scheme and as inline
    // code, so Discord makes no link and fetches no preview. The empty allowed_mentions stops every ping.
    public static DiscordMessage Create(SecurityWarning warning)
    {
        string name = FormatAsCode(warning.ServiceName, MaxNameLength);
        string text;

        if (warning.Kind == SecurityWarningKind.HeadersDisappeared)
        {
            text = name + " no longer sends: " + string.Join(SecurityHeaderRule.Separator, warning.DisappearedHeaders) + ".";
        }
        else if (warning.Kind == SecurityWarningKind.CertificateRejected)
        {
            text = "the TLS certificate of " + name + " was rejected.";
        }
        else if (warning.Kind == SecurityWarningKind.RedirectToOtherHost)
        {
            text = name + " now redirects to another domain: " + FormatAsCode(warning.RedirectTargetHost ?? "", MaxHostLength) + ".";
        }
        else if (warning.Kind == SecurityWarningKind.RedirectToInternalTarget)
        {
            text = name + " now redirects to a private or internal address.";
        }
        else if (warning.Kind == SecurityWarningKind.RedirectUnreadable)
        {
            text = name + " now sends a redirect whose target cannot be read.";
        }
        else
        {
            text = "the TLS certificate of " + name + " has at most " + DaysText(warning.ThresholdDays) + " left.";
        }

        return new DiscordMessage("DeadSwitch security warning: " + text);
    }

    private static string DaysText(int? days)
    {
        if (days == 1)
        {
            return "1 day";
        }

        return days + " days";
    }

    private static string ToText(ServiceStatus status)
    {
        return status.ToString().ToUpperInvariant();
    }

    // Security: a query can hold a token, and Discord has no reason to see it. Credentials are not
    // accepted by UrlSafety, but an old row could hold them, so they are left out here too.
    private static string RemoveEverythingAfterThePath(string url)
    {
        Uri? uri;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
        {
            return "(not shown)";
        }

        return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
    }

    // Security: Discord shows inline code as it is written, so markdown and links in the text do nothing,
    // and it fetches no preview of the address. The backtick is replaced, or the text could end the
    // code format itself. A line break would end it as well.
    private static string FormatAsCode(string text, int maxLength)
    {
        string shortened = Shorten(text, maxLength);

        StringBuilder cleaned = new StringBuilder();
        foreach (char character in shortened)
        {
            if (character == '`')
            {
                cleaned.Append('\'');
            }
            else if (char.IsControl(character) || IsLineSeparatorOrInvisible(character))
            {
                cleaned.Append(' ');
            }
            else
            {
                cleaned.Append(character);
            }
        }

        return "`" + cleaned + "`";
    }

    // Security: IsControl misses these. A line separator could end the code format, and an invisible
    // character could make a name look like something else. A joined emoji loses its joiner here.
    private static bool IsLineSeparatorOrInvisible(char character)
    {
        UnicodeCategory category = char.GetUnicodeCategory(character);

        return category == UnicodeCategory.LineSeparator
            || category == UnicodeCategory.ParagraphSeparator
            || category == UnicodeCategory.Format;
    }

    private static string Shorten(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        // An emoji is two chars in C#. Cutting between them would leave half a character.
        int length = maxLength;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text.Substring(0, length) + "...";
    }
}

internal sealed class DiscordAllowedMentions
{
    // An empty list means: find no mentions of any kind. That covers @everyone, @here, users and roles.
    [JsonPropertyName("parse")]
    public string[] Parse { get; } = Array.Empty<string>();
}
