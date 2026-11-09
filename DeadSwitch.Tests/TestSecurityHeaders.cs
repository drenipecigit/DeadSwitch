using System.Net.Http.Headers;

namespace DeadSwitch.Tests;

// The names are written out here and not read from SecurityHeaderRule. If the rule ever loses a
// name, the tests must notice it instead of following along.
internal static class TestSecurityHeaders
{
    public const string StrictTransportSecurity = "Strict-Transport-Security";
    public const string ContentSecurityPolicy = "Content-Security-Policy";
    public const string ContentTypeOptions = "X-Content-Type-Options";
    public const string FrameOptions = "X-Frame-Options";
    public const string ReferrerPolicy = "Referrer-Policy";

    public const string OrdinaryValue = "test-value";

    public static readonly string[] AllNames =
    {
        StrictTransportSecurity,
        ContentSecurityPolicy,
        ContentTypeOptions,
        FrameOptions,
        ReferrerPolicy,
    };

    public static void AddAll(HttpResponseHeaders headers, string value)
    {
        foreach (string name in AllNames)
        {
            headers.TryAddWithoutValidation(name, value);
        }
    }

    public static void AddAllExcept(HttpResponseHeaders headers, string excludedName)
    {
        foreach (string name in AllNames)
        {
            if (name != excludedName)
            {
                headers.TryAddWithoutValidation(name, OrdinaryValue);
            }
        }
    }
}
