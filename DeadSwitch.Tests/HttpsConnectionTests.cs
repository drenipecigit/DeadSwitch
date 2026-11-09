using System.Net;
using System.Security.Cryptography.X509Certificates;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Infrastructure.Monitoring;
using Microsoft.Extensions.Time.Testing;

namespace DeadSwitch.Tests;

// Real TLS handshakes through the real connector and the real handler, against a server on 127.0.0.1.
//
// The connector is built here with a rule that allows only 127.0.0.1, in the same way as in
// SsrfProtectionTests. Each test also says which certificate its own handler trusts. The application
// has no setting for either. The URLs use 127.0.0.1, so no name is looked up and nothing leaves the
// machine.
public class HttpsConnectionTests
{
    private const string LocalHost = "127.0.0.1";
    private static readonly IPAddress LocalAddress = IPAddress.Parse(LocalHost);

    private readonly FakeTimeProvider _clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

    [Fact]
    public async Task AValidCertificateIsRecordedWithItsExpiryDate()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        await using LocalTestServer server = await LocalTestServer.StartHttpsAsync(certificate);
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);
        using HttpClient client = CreateClient(recorder, certificate);

        using HttpResponseMessage response = await client.GetAsync(UrlOf(server, "/ok"));
        TlsHandshake? handshake = recorder.Take(LocalHost);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(handshake);
        Assert.False(handshake.IsRejected);
        Assert.Equal(certificate.NotAfter.ToUniversalTime(), handshake.NotAfterUtc);
    }

    [Fact]
    public async Task AnExpiredCertificateIsRejectedBeforeAnyRequestIsSent()
    {
        using X509Certificate2 certificate = TestCertificates.CreateExpiredForLocalAddress();

        await AssertRejectedAsync(certificate, certificate);
    }

    [Fact]
    public async Task ACertificateForAnotherNameIsRejectedBeforeAnyRequestIsSent()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForOtherName();

        await AssertRejectedAsync(certificate, certificate);
    }

    [Fact]
    public async Task ACertificateThatIsNotTrustedIsRejectedBeforeAnyRequestIsSent()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        using X509Certificate2 somethingElse = TestCertificates.CreateForLocalAddress();

        await AssertRejectedAsync(certificate, somethingElse);
    }

    // The connection is reused, so no handshake happens and nothing can be recorded. This is the
    // normal case on a reused connection and not a sign of a bad certificate.
    [Fact]
    public async Task AReusedConnectionHasNoNewHandshake()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        await using LocalTestServer server = await LocalTestServer.StartHttpsAsync(certificate);
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);
        using HttpClient client = CreateClient(recorder, certificate);

        using HttpResponseMessage first = await client.GetAsync(UrlOf(server, "/ok"));
        Assert.NotNull(recorder.Take(LocalHost));
        using HttpResponseMessage second = await client.GetAsync(UrlOf(server, "/ok"));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Null(recorder.Take(LocalHost));
        Assert.Equal(server.Requests[0].ConnectionId, server.Requests[1].ConnectionId);
    }

    [Fact]
    public async Task ConnectionCloseGivesOneHandshakePerRequest()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        await using LocalTestServer server = await LocalTestServer.StartHttpsAsync(certificate);
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);
        using HttpClient client = CreateClient(recorder, certificate);

        await SendWithConnectionCloseAsync(client, server);
        Assert.NotNull(recorder.Take(LocalHost));
        await SendWithConnectionCloseAsync(client, server);
        Assert.NotNull(recorder.Take(LocalHost));

        Assert.NotEqual(server.Requests[0].ConnectionId, server.Requests[1].ConnectionId);
    }

    [Fact]
    public async Task AGradeCanBeSetFromARealHttpsResponse()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        await using LocalTestServer server = await LocalTestServer.StartHttpsAsync(certificate);
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);
        using HttpClient client = CreateClient(recorder, certificate);
        Uri address = new Uri(UrlOf(server, "/security-headers"));

        using HttpResponseMessage response = await client.GetAsync(address);
        SecurityHeaderCheck check = SecurityHeaderReader.Read(address, (int)response.StatusCode, response.Headers);

        Assert.Equal("A", check.Grade);
    }

    // The handler the application uses keeps blocking the address, also when the scheme is https.
    [Fact]
    public async Task TheProductionHandlerBlocksLoopbackOverHttps()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        await using LocalTestServer server = await LocalTestServer.StartHttpsAsync(certificate);
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);
        using HttpClient client = new HttpClient(ProtectedHttpHandler.Create(recorder));

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(UrlOf(server, "/ok")));

        Assert.IsType<DestinationBlockedException>(exception.InnerException);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public void TheProductionHandlerTrustsNothingExtra()
    {
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);

        using SocketsHttpHandler handler = ProtectedHttpHandler.Create(recorder);

        Assert.Null(handler.SslOptions.CertificateChainPolicy);
        Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.NotNull(handler.ConnectCallback);
    }

    private async Task AssertRejectedAsync(X509Certificate2 serverCertificate, X509Certificate2 trustedCertificate)
    {
        await using LocalTestServer server = await LocalTestServer.StartHttpsAsync(serverCertificate);
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);
        using HttpClient client = CreateClient(recorder, trustedCertificate);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(UrlOf(server, "/ok")));
        TlsHandshake? handshake = recorder.Take(LocalHost);

        Assert.Equal(0, server.RequestCount);
        Assert.NotNull(handshake);
        Assert.True(handshake.IsRejected);
        Assert.Null(handshake.NotAfterUtc);
    }

    private static async Task SendWithConnectionCloseAsync(HttpClient client, LocalTestServer server)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, UrlOf(server, "/ok"));
        request.Headers.ConnectionClose = true;

        using HttpResponseMessage response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
    }

    private static string UrlOf(LocalTestServer server, string path)
    {
        return $"https://{LocalHost}:{server.Port}{path}";
    }

    private static HttpClient CreateClient(TlsHandshakeRecorder recorder, X509Certificate2 trustedCertificate)
    {
        PublicInternetConnector connector = new PublicInternetConnector(AnswerWithLocalAddress, AllowOnlyLocalAddress);
        SocketsHttpHandler handler = ProtectedHttpHandler.Create(connector, recorder);
        handler.SslOptions.CertificateChainPolicy = TestCertificates.TrustOnly(trustedCertificate);
        return new HttpClient(handler);
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
