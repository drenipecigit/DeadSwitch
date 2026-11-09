using System.Net;
using System.Security.Cryptography.X509Certificates;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Monitoring;

namespace DeadSwitch.Tests;

// The real probe and real TLS handshakes against a server on 127.0.0.1. The handler's connection step is
// replaced by LocalServerConnectStep, and every test says which certificate its own handler trusts.
public class RealTlsProbeTests
{
    private const string BaseUrl = "https://monitored.test";
    private const int TimeoutSeconds = 10;

    private readonly TlsHandshakeRecorder _recorder = new TlsHandshakeRecorder(TimeProvider.System);

    [Fact]
    public async Task ACheckGetsDateAndGradeFromOneRealHttpsResponse()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForMonitoredHosts();
        await using LocalTestServer server = await LocalTestServer.StartHttpsAsync(certificate);
        using HttpClient client = CreateClient(server, certificate);
        HttpHealthProbe probe = new HttpHealthProbe(client, _recorder);

        ProbeResult result = await probe.CheckAsync($"{BaseUrl}/security-headers", TimeoutSeconds, CancellationToken.None);

        Assert.Equal(ServiceStatus.Up, result.Status);
        Assert.Equal(1, server.RequestCount);
        Assert.NotNull(result.SecurityHeaders);
        Assert.Equal("A", result.SecurityHeaders.Grade);
        Assert.NotNull(result.Certificate);
        Assert.Equal(certificate.NotAfter.ToUniversalTime(), result.Certificate.NotAfterUtc);
    }

    [Fact]
    public async Task ACertificateThatIsNotTrustedGivesDownAndNoGrade()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForMonitoredHosts();
        using X509Certificate2 somethingElse = TestCertificates.CreateForMonitoredHosts();

        await AssertInvalidAsync(certificate, somethingElse);
    }

    [Fact]
    public async Task ACertificateForAnotherNameGivesDownAndNoGrade()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForOtherName();

        await AssertInvalidAsync(certificate, certificate);
    }

    [Fact]
    public async Task TwoChecksInARowEachGetTheirOwnHandshake()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForMonitoredHosts();
        await using LocalTestServer server = await LocalTestServer.StartHttpsAsync(certificate);
        using HttpClient client = CreateClient(server, certificate);
        HttpHealthProbe probe = new HttpHealthProbe(client, _recorder);

        ProbeResult first = await probe.CheckAsync($"{BaseUrl}/ok", TimeoutSeconds, CancellationToken.None);
        ProbeResult second = await probe.CheckAsync($"{BaseUrl}/ok", TimeoutSeconds, CancellationToken.None);

        Assert.NotNull(first.Certificate);
        Assert.NotNull(first.Certificate.NotAfterUtc);
        Assert.NotNull(second.Certificate);
        Assert.NotNull(second.Certificate.NotAfterUtc);
        Assert.NotEqual(server.Requests[0].ConnectionId, server.Requests[1].ConnectionId);
    }

    // The server offers HTTP/2, so a request that was allowed to use it would get it.
    [Fact]
    public async Task TheServerSeesHttp11AndAClosedConnection()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForMonitoredHosts();
        await using LocalTestServer server = await LocalTestServer.StartHttpsAsync(certificate);
        using HttpClient client = CreateClient(server, certificate);
        HttpHealthProbe probe = new HttpHealthProbe(client, _recorder);

        await probe.CheckAsync($"{BaseUrl}/ok", TimeoutSeconds, CancellationToken.None);

        Assert.Equal("HTTP/1.1", server.Requests[0].Protocol);
        Assert.Equal("close", server.Requests[0].ConnectionHeader);
    }

    // The two share one entry, so one of them may see nothing. Neither may be called invalid.
    [Fact]
    public async Task TwoChecksOfTheSameHostAtOnceAreNeverCalledInvalid()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForMonitoredHosts();
        await using LocalTestServer server = await LocalTestServer.StartHttpsAsync(certificate);
        using HttpClient client = CreateClient(server, certificate);
        HttpHealthProbe probe = new HttpHealthProbe(client, _recorder);

        Task<ProbeResult> first = probe.CheckAsync($"{BaseUrl}/held-first", TimeoutSeconds, CancellationToken.None);
        Task<ProbeResult> second = probe.CheckAsync($"{BaseUrl}/held-second", TimeoutSeconds, CancellationToken.None);
        ProbeResult firstResult = await first;
        ProbeResult secondResult = await second;

        Assert.Equal(ServiceStatus.Up, firstResult.Status);
        Assert.Equal(ServiceStatus.Up, secondResult.Status);
        AssertDateOrNothingSeen(firstResult, certificate);
        AssertDateOrNothingSeen(secondResult, certificate);
    }

    // The name in the URL, the name in the handshake and the entry must all be the same host.
    [Theory]
    [InlineData("https://monitored.test/ok")]
    [InlineData("https://MONITORED.test/ok")]
    [InlineData("https://monitored.test./ok")]
    [InlineData("https://b\u00FCcher.test/ok")]
    [InlineData("https://B\u00DCCHER.test/ok")]
    [InlineData("https://xn--bcher-kva.test/ok")]
    public async Task TheDateIsFoundHoweverTheHostIsWritten(string url)
    {
        using X509Certificate2 certificate = TestCertificates.CreateForMonitoredHosts();
        await using LocalTestServer server = await LocalTestServer.StartHttpsAsync(certificate);
        using HttpClient client = CreateClient(server, certificate);
        HttpHealthProbe probe = new HttpHealthProbe(client, _recorder);

        ProbeResult result = await probe.CheckAsync(url, TimeoutSeconds, CancellationToken.None);

        Assert.Equal(ServiceStatus.Up, result.Status);
        Assert.NotNull(result.Certificate);
        Assert.Equal(certificate.NotAfter.ToUniversalTime(), result.Certificate.NotAfterUtc);
    }

    private async Task AssertInvalidAsync(X509Certificate2 serverCertificate, X509Certificate2 trustedCertificate)
    {
        await using LocalTestServer server = await LocalTestServer.StartHttpsAsync(serverCertificate);
        using HttpClient client = CreateClient(server, trustedCertificate);
        HttpHealthProbe probe = new HttpHealthProbe(client, _recorder);

        ProbeResult result = await probe.CheckAsync($"{BaseUrl}/security-headers", TimeoutSeconds, CancellationToken.None);

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Equal("Connection, DNS or TLS failure.", result.FailureReason);
        Assert.Equal(0, server.RequestCount);
        Assert.Null(result.SecurityHeaders);
        Assert.NotNull(result.Certificate);
        Assert.Null(result.Certificate.NotAfterUtc);
        Assert.Equal(HttpHealthProbe.CertificateNotValidReason, result.Certificate.NotAssessedReason);
    }

    private static void AssertDateOrNothingSeen(ProbeResult result, X509Certificate2 certificate)
    {
        Assert.NotNull(result.Certificate);

        if (result.Certificate.NotAfterUtc != null)
        {
            Assert.Equal(certificate.NotAfter.ToUniversalTime(), result.Certificate.NotAfterUtc);
            Assert.Null(result.Certificate.NotAssessedReason);
        }
        else
        {
            Assert.Equal(HttpHealthProbe.NoNewHandshakeReason, result.Certificate.NotAssessedReason);
        }
    }

    private HttpClient CreateClient(LocalTestServer server, X509Certificate2 trustedCertificate)
    {
        return TlsTestClient.Create(new LocalServerConnectStep(server.Port), _recorder, trustedCertificate);
    }
}
