namespace DeadSwitch.Infrastructure.Notifications;

// Security-critical: decides where the application may send a message. The message goes wherever this
// address points, so a mistake here sends service names and the token to someone else. Each check below
// is kept on its own, even where another one would also catch the same address today: the protection
// must not depend on how the other checks happen to work. It is deliberately stricter than the real
// form needs: Discord's /slack and /github variants treat the body differently, so they are rejected.
public static class DiscordWebhookUrl
{
    private const string WebhookPathStart = "/api/webhooks/";

    public static bool IsAllowed(string url)
    {
        if (!HasOnlyPlainAscii(url))
        {
            return false;
        }

        Uri? uri;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
        {
            return false;
        }

        // Uri quietly rewrites what it reads: it removes "..", turns \ into /, removes spaces at the
        // ends and lowercases the host. The address must already be in the form Uri would send, so that
        // the address judged below is exactly the address that was configured.
        if (uri.AbsoluteUri != url)
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (uri.Port != 443)
        {
            return false;
        }

        // Credentials can hide the real host. A query or a fragment has no place in a webhook address,
        // and the notifier adds the query it needs itself.
        if (uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "")
        {
            return false;
        }

        // An exact comparison, so that "discord.com.example.com" and "ptb.discord.com" do not pass.
        if (uri.Host != "discord.com" && uri.Host != "discordapp.com")
        {
            return false;
        }

        return HasWebhookPath(uri.AbsolutePath);
    }

    // The path must be exactly /api/webhooks/{digits}/{token}.
    private static bool HasWebhookPath(string path)
    {
        if (!path.StartsWith(WebhookPathStart, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = path.Substring(WebhookPathStart.Length).Split('/');
        if (parts.Length != 2)
        {
            return false;
        }

        return HasOnlyDigits(parts[0]) && IsPlainToken(parts[1]);
    }

    // Letters from other alphabets can look the same as the real host, and control characters and
    // spaces have no place in the address. A real webhook address never needs any of them.
    private static bool HasOnlyPlainAscii(string text)
    {
        foreach (char character in text)
        {
            // The space (0x20) and everything below it are the space and the control characters.
            // Everything above '~' (0x7E) is DEL (0x7F) and every character outside ASCII.
            // What is left is the visible ASCII characters, 0x21 to 0x7E.
            if (character <= ' ' || character > '~')
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasOnlyDigits(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (char character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    // Stops %2F, which could otherwise be read as a further part of the path.
    private static bool IsPlainToken(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (char character in text)
        {
            bool isAllowed = char.IsAsciiLetterOrDigit(character) || character == '-' || character == '_';
            if (!isAllowed)
            {
                return false;
            }
        }

        return true;
    }
}
