using System.Net;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Monitoring;

namespace DeadSwitch.Tests;

// Runs the real probe, the real handler and the real connector, with a fake DNS and no connection.
// Shows what happens to unusual hosts and failures on the way through, so that no exception gets out.
// The trait is there in case a run removes ConnectCallback: the names would then go to the real DNS.
public class ProbeFailureMappingTests
{
    private const string NameLookupWithoutConnector = "NameLookupWithoutConnector";

    // HttpClient wraps whatever the connection step throws in an HttpRequestException. Without that,
    // Dns.GetHostAddressesAsync would throw ArgumentOutOfRangeException out of the probe for a long name.
    [Fact]
    [Trait("Category", NameLookupWithoutConnector)]
    public async Task ArgumentOutOfRangeFromTheDnsStepIsRecordedAsDown()
    {
        ProbeResult result = await CheckWithDnsAsync("http://example.com/", ThrowArgumentOutOfRangeAsync);

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Equal("Connection, DNS or TLS failure.", result.FailureReason);
    }

    [Fact]
    [Trait("Category", NameLookupWithoutConnector)]
    public async Task ArgumentExceptionFromTheDnsStepIsRecordedAsDown()
    {
        ProbeResult result = await CheckWithDnsAsync("http://example.com/", ThrowArgumentExceptionAsync);

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Equal("Connection, DNS or TLS failure.", result.FailureReason);
    }

    // These names pass UrlSafety and have no address in the fake DNS.
    [Theory]
    [Trait("Category", NameLookupWithoutConnector)]
    [MemberData(nameof(UnusualNames))]
    public async Task UnusualNameThatPassesValidationIsRecordedAsDown(string url)
    {
        ProbeResult result = await CheckWithDnsAsync(url, AnswerWithNoAddressAsync);

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Equal("Connection, DNS or TLS failure.", result.FailureReason);
    }

    public static TheoryData<string> UnusualNames()
    {
        string longLabel = new string('a', 64);
        string partOfLongName = new string('a', 60);

        return new TheoryData<string>
        {
            // One label longer than DNS allows.
            "http://" + longLabel + ".example.com/",
            "https://" + longLabel + ".example.com/",
            "http://" + new string('ä', 64) + ".example.com/",

            // A whole name longer than 255 characters.
            "http://" + partOfLongName + "." + partOfLongName + "." + partOfLongName + "." + partOfLongName + "." + partOfLongName + ".example.com/",

            // Names that look like the result of an IDN conversion, but are not valid ones.
            "http://xn--a.example.com/",
            "http://xn--/",
            "http://xn---.example.com/",

            // Right-to-left letters mixed with ordinary letters, and a label with odd hyphens.
            "http://אa.example.com/",
            "http://-a-.example.com/"
        };
    }

    private static async Task<ProbeResult> CheckWithDnsAsync(string url, Func<string, CancellationToken, Task<IPAddress[]>> resolveHost)
    {
        PublicInternetConnector connector = new PublicInternetConnector(resolveHost, UrlSafety.IsPublicAddress);
        using HttpClient client = new HttpClient(ProtectedHttpHandler.Create(connector));
        HttpHealthProbe probe = new HttpHealthProbe(client);
        return await probe.CheckAsync(url, 2, CancellationToken.None);
    }

    private static Task<IPAddress[]> ThrowArgumentOutOfRangeAsync(string host, CancellationToken cancellationToken)
    {
        throw new ArgumentOutOfRangeException(nameof(host));
    }

    private static Task<IPAddress[]> ThrowArgumentExceptionAsync(string host, CancellationToken cancellationToken)
    {
        throw new ArgumentException("The test made the DNS step fail.");
    }

    private static Task<IPAddress[]> AnswerWithNoAddressAsync(string host, CancellationToken cancellationToken)
    {
        return Task.FromResult(Array.Empty<IPAddress>());
    }
}
