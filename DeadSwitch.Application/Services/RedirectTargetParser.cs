using System.Net;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;

namespace DeadSwitch.Application.Services;

// Security-critical: reads a value written by a server we do not control. Nothing here looks up a name
// or opens a connection. The result holds a fixed category and at most a host name that has passed the
// allowlist, so the value the server sent ends here.
//
// Longer than ordinary code on purpose. Browsers and .NET read odd values differently, so anything that
// could be read in two ways is called Unreadable instead of guessed at. Every step guards against a
// real trick, and the steps must stay in this order.
public static class RedirectTargetParser
{
    private const int MaxLocationLength = 2048;
    private const int MaxHostLength = 253;

    private const string HttpStart = "http://";
    private const string HttpsStart = "https://";

    private static readonly RedirectTargetCheck SameHost = new RedirectTargetCheck(RedirectFinding.SameHost, null);
    private static readonly RedirectTargetCheck InternalTarget = new RedirectTargetCheck(RedirectFinding.InternalTarget, null);
    private static readonly RedirectTargetCheck Unreadable = new RedirectTargetCheck(RedirectFinding.Unreadable, null);

    // Names that are normally only used inside a network. The list cannot be complete, because finding
    // out what a name really points to would need a lookup, and a redirect target must never be looked up.
    private static readonly string[] InternalNameEndings =
    {
        ".local",
        ".internal",
        ".intranet",
        ".lan",
        ".corp",
        ".home",
        ".home.arpa",
    };

    // Returns null when the response has no value to read.
    public static RedirectTargetCheck? Parse(Uri requestUri, IReadOnlyList<string> locationValues)
    {
        if (locationValues.Count == 0)
        {
            return null;
        }

        // Browsers disagree about which of several values counts, so none of them can be trusted.
        if (locationValues.Count > 1)
        {
            return Unreadable;
        }

        string value = locationValues[0];

        if (value.Length == 0)
        {
            return null;
        }

        if (value.Length > MaxLocationLength || HasUnsafeCharacter(value))
        {
            return Unreadable;
        }

        return ParseCleanValue(requestUri, value);
    }

    // The form is decided from the characters alone, and Uri is not used. On Linux Uri.TryCreate can read
    // "/a" as the file address file:///a, so Uri must never be the one to say whether a value is relative.
    public static bool IsRelativeReference(string value)
    {
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        // A colon before any of the other three means that the value starts with a scheme.
        int firstDelimiter = value.IndexOfAny(new char[] { ':', '/', '?', '#' });

        if (firstDelimiter == -1)
        {
            return true;
        }

        return value[firstDelimiter] != ':';
    }

    // Browsers remove some of these before reading a value and treat a backslash as a slash, so what
    // the browser sees would not be what is read here. Above 0x7E the bytes have been decoded as
    // Latin-1 by .NET, so the value that was really meant cannot be known.
    private static bool HasUnsafeCharacter(string value)
    {
        foreach (char character in value)
        {
            if (character <= ' ' || character >= 0x7F || character == '\\')
            {
                return true;
            }
        }

        return false;
    }

    private static RedirectTargetCheck ParseCleanValue(Uri requestUri, string value)
    {
        // Browsers skip the extra slashes and still find a host. Also stopped by the check further down,
        // and kept so that the protection does not depend on how that check happens to work.
        if (value.StartsWith("///", StringComparison.Ordinal))
        {
            return Unreadable;
        }

        string absoluteValue;

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            absoluteValue = requestUri.Scheme + ":" + value;
        }
        else if (IsRelativeReference(value))
        {
            return SameHost;
        }
        else
        {
            absoluteValue = value;
        }

        // Must come before Uri.TryCreate: it is what makes sure that Uri only ever sees http and https.
        if (!StartsWithHttpAndAHost(absoluteValue))
        {
            return Unreadable;
        }

        Uri? target;
        if (!Uri.TryCreate(absoluteValue, UriKind.Absolute, out target))
        {
            return Unreadable;
        }

        // .NET and browsers can pick different hosts when there is more than one @.
        if (CountAtSignsInAuthority(absoluteValue) > 1)
        {
            return Unreadable;
        }

        return ClassifyHost(requestUri, target);
    }

    private static bool StartsWithHttpAndAHost(string value)
    {
        string start;

        if (value.StartsWith(HttpsStart, StringComparison.OrdinalIgnoreCase))
        {
            start = HttpsStart;
        }
        else if (value.StartsWith(HttpStart, StringComparison.OrdinalIgnoreCase))
        {
            start = HttpStart;
        }
        else
        {
            return false;
        }

        if (value.Length == start.Length)
        {
            return false;
        }

        return value[start.Length] != '/';
    }

    private static int CountAtSignsInAuthority(string absoluteValue)
    {
        int count = 0;

        // Past the scheme and its slashes. The value is known to start with http:// or https://.
        int position = absoluteValue.IndexOf("//", StringComparison.Ordinal) + 2;

        while (position < absoluteValue.Length)
        {
            char character = absoluteValue[position];

            if (character == '/' || character == '?' || character == '#')
            {
                break;
            }

            if (character == '@')
            {
                count++;
            }

            position++;
        }

        return count;
    }

    private static RedirectTargetCheck ClassifyHost(Uri requestUri, Uri target)
    {
        string? host = NormalizeHost(target);
        if (host == null)
        {
            return Unreadable;
        }

        IPAddress? address;
        bool isAddress = IPAddress.TryParse(host, out address);

        if (!isAddress)
        {
            // Browsers try to read such a name as an address and refuse it when they cannot.
            if (HasNumericLastLabel(host))
            {
                return Unreadable;
            }

            if (!IsWellFormedName(host))
            {
                return Unreadable;
            }
        }

        // The service's own host goes through the same steps, or the two could never be compared.
        if (host == NormalizeHost(requestUri))
        {
            return SameHost;
        }

        if (!UrlSafety.HasPublicHost(target))
        {
            return InternalTarget;
        }

        if (!isAddress && LooksInternal(host))
        {
            return InternalTarget;
        }

        return new RedirectTargetCheck(RedirectFinding.OtherHost, host);
    }

    // IdnHost is the name a connection would use, so the same name is judged here as in UrlSafety.
    // An address is rewritten in its shortest form, so that one address always has one spelling.
    private static string? NormalizeHost(Uri uri)
    {
        string? connectionHost = UrlSafety.GetConnectionHost(uri);
        if (connectionHost == null)
        {
            return null;
        }

        string host = connectionHost.Trim('[', ']').ToLowerInvariant();

        // Only one dot: a name that ends with two is not a valid name, and the check of labels stops it.
        if (host.EndsWith('.'))
        {
            host = host.Substring(0, host.Length - 1);
        }

        IPAddress? address;
        if (IPAddress.TryParse(host, out address))
        {
            return address.ToString();
        }

        // Only when something that looks like a domain is left, so that www.com stays www.com.
        if (host.StartsWith("www.", StringComparison.Ordinal) && host.IndexOf('.', 4) != -1)
        {
            return host.Substring(4);
        }

        return host;
    }

    private static bool HasNumericLastLabel(string host)
    {
        string lastLabel = host.Substring(host.LastIndexOf('.') + 1);

        if (lastLabel.StartsWith("0x", StringComparison.Ordinal))
        {
            return IsAllHexDigits(lastLabel.Substring(2));
        }

        return lastLabel.Length > 0 && IsAllDigits(lastLabel);
    }

    private static bool IsAllDigits(string text)
    {
        foreach (char character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllHexDigits(string text)
    {
        foreach (char character in text)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    // Security: an allowlist, and the reason a host is safe to show. Without @, backtick, brackets, stars
    // or spaces it cannot mention anyone or change how a message or a page looks.
    private static bool IsWellFormedName(string host)
    {
        if (host.Length == 0 || host.Length > MaxHostLength)
        {
            return false;
        }

        if (host.StartsWith('.') || host.EndsWith('.') || host.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char character in host)
        {
            bool isAllowed = char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '.' || character == '-';

            if (!isAllowed)
            {
                return false;
            }
        }

        return true;
    }

    // A name without a dot can only be found inside a network.
    private static bool LooksInternal(string host)
    {
        if (!host.Contains('.'))
        {
            return true;
        }

        foreach (string ending in InternalNameEndings)
        {
            if (host.EndsWith(ending, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
