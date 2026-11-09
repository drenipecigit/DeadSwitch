using System.Net;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Infrastructure.Monitoring;

namespace DeadSwitch.Tests;

// Real responses through the real SocketsHttpHandler, from a local server on 127.0.0.1.
//
// The connector is built here with a rule that allows only 127.0.0.1, in the same way as in
// SsrfProtectionTests. The application itself cannot do this: the Create method that takes a connector
// is internal, and RegisteredHandlerBlocksLoopbackAddress proves that the registered handler still
// blocks this address.
//
// The URLs use 127.0.0.1 and no name, so no name is ever looked up and nothing can leave the machine.
//
// The probe cannot be used here, because UrlSafety stops 127.0.0.1 before any request is sent. The
// reader is called directly instead. GradedAddress only says "this was https" to the reader, and no
// traffic goes there.
public class SecurityHeaderHandlerTests
{
    private static readonly IPAddress LocalAddress = IPAddress.Parse("127.0.0.1");
    private static readonly Uri GradedAddress = new Uri("https://example.test/");

    [Fact]
    public async Task AllFiveHeadersAreReadFromARealResponse()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        using HttpClient client = CreateClient();

        using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{server.Port}/security-headers");
        SecurityHeaderCheck check = SecurityHeaderReader.Read(GradedAddress, 200, response.Headers);

        Assert.Equal("A", check.Grade);
        Assert.Equal("", check.MissingHeaders);
    }

    // The name starts with "Content-", but it is not one of the content headers, which live elsewhere
    // in HttpResponseMessage. Only a real response can show which group the handler puts it in.
    [Fact]
    public async Task ContentSecurityPolicyIsAnOrdinaryResponseHeader()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        using HttpClient client = CreateClient();

        using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{server.Port}/security-headers");

        Assert.True(response.Headers.Contains(TestSecurityHeaders.ContentSecurityPolicy));
        Assert.False(response.Content.Headers.Contains(TestSecurityHeaders.ContentSecurityPolicy));
    }

    [Theory]
    [InlineData(TestSecurityHeaders.StrictTransportSecurity)]
    [InlineData(TestSecurityHeaders.ContentSecurityPolicy)]
    [InlineData(TestSecurityHeaders.ContentTypeOptions)]
    [InlineData(TestSecurityHeaders.FrameOptions)]
    [InlineData(TestSecurityHeaders.ReferrerPolicy)]
    public async Task AHeaderWithOnlySpacesCountsAsMissing(string blankName)
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        using HttpClient client = CreateClient();

        using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{server.Port}/blank-security-header?name={blankName}");
        SecurityHeaderCheck check = SecurityHeaderReader.Read(GradedAddress, 200, response.Headers);

        Assert.Equal("B", check.Grade);
        Assert.Equal(blankName, check.MissingHeaders);
    }

    [Fact]
    public async Task HeadersWithinTheLimitAreRead()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        using HttpClient client = CreateClient();

        using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{server.Port}/large-header?kilobytes=60");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HeadersOverTheLimitAreRefused()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        using HttpClient client = CreateClient();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync($"http://127.0.0.1:{server.Port}/large-header?kilobytes=70"));
    }

    private static HttpClient CreateClient()
    {
        PublicInternetConnector connector = new PublicInternetConnector(AnswerWithLocalAddress, AllowOnlyLocalAddress);
        return new HttpClient(ProtectedHttpHandler.Create(connector));
    }

    private static Task<IPAddress[]> AnswerWithLocalAddress(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = { LocalAddress };
        return Task.FromResult(addresses);
    }

    private static bool AllowOnlyLocalAddress(IPAddress address)
    {
        return address.Equals(LocalAddress);
    }
}
