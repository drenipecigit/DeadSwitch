using System.Net.Http.Headers;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;

namespace DeadSwitch.Infrastructure.Monitoring;

// Security: the values come from a server we do not control. They are only looked at to see whether
// anything is there, and are never kept. What gets saved is built from our own fixed list of names,
// so a hostile or very long value cannot reach the database, the API or the dashboard.
public static class SecurityHeaderReader
{
    public const string NotHttpsReason = "Not an HTTPS address.";
    public const string RedirectReason = "The response was a redirect.";
    public const string NotSuccessReason = "The response was not a success.";

    public static SecurityHeaderCheck Read(Uri uri, int statusCode, HttpResponseHeaders headers)
    {
        // Comes first: Strict-Transport-Security has no meaning over http, whatever the answer was.
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return NotAssessed(NotHttpsReason);
        }

        // These headers describe the redirect and not the page, which would give a misleading grade.
        if (statusCode >= 300 && statusCode <= 399)
        {
            return NotAssessed(RedirectReason);
        }

        // An error page often comes from another layer, such as a proxy, and not from the service.
        if (statusCode < 200 || statusCode > 299)
        {
            return NotAssessed(NotSuccessReason);
        }

        List<string> missingHeaders = new List<string>();

        foreach (string name in SecurityHeaderRule.CheckedHeaders)
        {
            if (!HasValue(headers, name))
            {
                missingHeaders.Add(name);
            }
        }

        string grade = SecurityHeaderRule.GradeFor(missingHeaders.Count);
        string missingText = string.Join(SecurityHeaderRule.Separator, missingHeaders);

        return new SecurityHeaderCheck(grade, missingText, null);
    }

    private static SecurityHeaderCheck NotAssessed(string reason)
    {
        return new SecurityHeaderCheck(null, null, reason);
    }

    // A header that holds nothing protects nothing, so it counts as missing.
    private static bool HasValue(HttpResponseHeaders headers, string name)
    {
        IEnumerable<string>? values;

        if (!headers.TryGetValues(name, out values))
        {
            return false;
        }

        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
        }

        return false;
    }
}
