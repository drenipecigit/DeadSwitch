using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Monitoring;
using Microsoft.Extensions.Time.Testing;

namespace DeadSwitch.Tests;

// The real probe with a stand-in for the network, and entries placed in the recorder by the test. This
// is how a case that depends on timing, such as another check's entry, can be tested exactly.
public class CertificateProbeTests : IDisposable
{
    private const string HttpsUrl = "https://monitored.test/";
    private const string HttpUrl = "http://monitored.test/";
    private const string Host = TestCertificates.MonitoredHost;
    private const string OtherHost = "other.test";

    private readonly FakeTimeProvider _clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly X509Certificate2 _certificate = TestCertificates.CreateForLocalAddress();
    private readonly TlsHandshakeRecorder _recorder;

    public CertificateProbeTests()
    {
        _recorder = new TlsHandshakeRecorder(_clock);
    }

    public void Dispose()
    {
        _certificate.Dispose();
    }

    [Fact]
    public async Task AnAcceptedEntryGivesTheDate()
    {
        SaveAcceptedEntry(Host);

        ProbeResult result = await CheckAsync(Answering(HttpStatusCode.OK), HttpsUrl);

        Assert.NotNull(result.Certificate);
        Assert.Equal(_certificate.NotAfter.ToUniversalTime(), result.Certificate.NotAfterUtc);
        Assert.Null(result.Certificate.NotAssessedReason);
    }

    // Point 1: a response was received, so an entry can never turn it into an invalid certificate.
    [Theory]
    [InlineData(HttpStatusCode.OK, ServiceStatus.Up)]
    [InlineData(HttpStatusCode.Found, ServiceStatus.Up)]
    [InlineData(HttpStatusCode.InternalServerError, ServiceStatus.Down)]
    public async Task ARejectedEntryNeverChangesAReceivedResponse(HttpStatusCode code, ServiceStatus expectedStatus)
    {
        SaveRejectedEntry(Host);

        ProbeResult result = await CheckAsync(Answering(code), HttpsUrl);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal((int)code, result.StatusCode);
        AssertNoNewHandshake(result);
    }

    [Fact]
    public async Task ARejectedEntryDoesNotChangeTheFailureReasonOfAResponse()
    {
        SaveRejectedEntry(Host);

        ProbeResult result = await CheckAsync(Answering(HttpStatusCode.OK), HttpsUrl);

        Assert.Null(result.FailureReason);
        Assert.NotNull(result.SecurityHeaders);
    }

    [Fact]
    public async Task ARejectedEntryWithATimeoutIsNotCalledInvalid()
    {
        SaveRejectedEntry(Host);
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.NeverAnswers = true;

        ProbeResult result = await CheckAsync(handler, HttpsUrl);

        Assert.Equal("Check timed out.", result.FailureReason);
        Assert.Null(result.Certificate);
    }

    [Fact]
    public async Task ARejectedEntryWithAnotherConnectionFailureIsNotCalledInvalid()
    {
        SaveRejectedEntry(Host);

        ProbeResult result = await CheckAsync(FailingWith(ConnectionFailure()), HttpsUrl);

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Null(result.Certificate);
    }

    [Fact]
    public async Task ATlsFailureWithARejectedEntryIsInvalidAndGetsNoGrade()
    {
        SaveRejectedEntry(Host);

        ProbeResult result = await CheckAsync(FailingWith(TlsFailure()), HttpsUrl);

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Null(result.SecurityHeaders);
        Assert.NotNull(result.Certificate);
        Assert.Null(result.Certificate.NotAfterUtc);
        Assert.Equal(HttpHealthProbe.CertificateNotValidReason, result.Certificate.NotAssessedReason);
    }

    [Fact]
    public async Task ATlsFailureWithoutAnEntryIsNotCalledInvalid()
    {
        ProbeResult result = await CheckAsync(FailingWith(TlsFailure()), HttpsUrl);

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Null(result.Certificate);
    }

    [Fact]
    public async Task ATlsFailureWithAnAcceptedEntryIsNotCalledInvalid()
    {
        SaveAcceptedEntry(Host);

        ProbeResult result = await CheckAsync(FailingWith(TlsFailure()), HttpsUrl);

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Null(result.Certificate);
    }

    // Point 2: every way out of a check clears the entry, so it cannot be read by a later check.
    [Fact]
    public async Task TheEntryIsGoneAfterASuccess()
    {
        await AssertEntryIsGoneAfterAsync(Answering(HttpStatusCode.OK));
    }

    [Fact]
    public async Task TheEntryIsGoneAfterAnErrorResponse()
    {
        await AssertEntryIsGoneAfterAsync(Answering(HttpStatusCode.InternalServerError));
    }

    [Fact]
    public async Task TheEntryIsGoneAfterATlsFailure()
    {
        await AssertEntryIsGoneAfterAsync(FailingWith(TlsFailure()));
    }

    [Fact]
    public async Task TheEntryIsGoneAfterAConnectionFailure()
    {
        await AssertEntryIsGoneAfterAsync(FailingWith(ConnectionFailure()));
    }

    [Fact]
    public async Task TheEntryIsGoneAfterATimeout()
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.NeverAnswers = true;

        await AssertEntryIsGoneAfterAsync(handler);
    }

    [Fact]
    public async Task TheEntryIsGoneAfterABlockedAddress()
    {
        await AssertEntryIsGoneAfterAsync(FailingWith(new HttpRequestException("blocked", new DestinationBlockedException())));
    }

    [Fact]
    public async Task TheEntryIsGoneWhenTheCallerCancels()
    {
        SaveAcceptedEntry(Host);
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.NeverAnswers = true;
        using HttpClient client = new HttpClient(handler);
        HttpHealthProbe probe = new HttpHealthProbe(client, _recorder);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.CheckAsync(HttpsUrl, 5, cancellation.Token));

        Assert.Null(_recorder.Take(Host));
    }

    [Fact]
    public async Task AnUnexpectedExceptionAlsoClearsTheEntry()
    {
        SaveAcceptedEntry(Host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CheckAsync(FailingWith(new InvalidOperationException("unexpected")), HttpsUrl));

        Assert.Null(_recorder.Take(Host));
    }

    [Fact]
    public async Task AnEntryAtTheMaximumAgeIsUsed()
    {
        SaveAcceptedEntry(Host);
        _clock.Advance(TimeSpan.FromMinutes(3));

        ProbeResult result = await CheckAsync(Answering(HttpStatusCode.OK), HttpsUrl);

        Assert.NotNull(result.Certificate);
        Assert.NotNull(result.Certificate.NotAfterUtc);
    }

    [Fact]
    public async Task AnEntryOverTheMaximumAgeIsNotUsed()
    {
        SaveAcceptedEntry(Host);
        _clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromTicks(1));

        ProbeResult result = await CheckAsync(Answering(HttpStatusCode.OK), HttpsUrl);

        AssertNoNewHandshake(result);
    }

    // No handshake happens over http, so an entry for this host belongs to some other check.
    [Fact]
    public async Task HttpSaysSoAndLeavesAnotherChecksEntryAlone()
    {
        SaveAcceptedEntry(Host);

        ProbeResult result = await CheckAsync(Answering(HttpStatusCode.OK), HttpUrl);

        Assert.NotNull(result.Certificate);
        Assert.Null(result.Certificate.NotAfterUtc);
        Assert.Equal(SecurityHeaderReader.NotHttpsReason, result.Certificate.NotAssessedReason);
        Assert.NotNull(_recorder.Take(Host));
    }

    [Fact]
    public async Task ARejectedUrlSaysNothingAboutACertificate()
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();

        ProbeResult result = await CheckAsync(handler, "https://127.0.0.1/");

        Assert.Equal(0, handler.Requests);
        Assert.Null(result.Certificate);
    }

    // Point 6: Connection: close does not exist in HTTP/2, so a higher version would mean reused connections.
    [Fact]
    public async Task TheProbeAsksForExactlyHttp11AndAClosedConnection()
    {
        ScriptedHttpHandler handler = Answering(HttpStatusCode.OK);

        await CheckAsync(handler, HttpsUrl);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpVersion.Version11, handler.LastRequest.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, handler.LastRequest.VersionPolicy);
        Assert.True(handler.LastRequest.Headers.ConnectionClose);
    }

    // Point 7: an entry that cannot be matched to this host only means that nothing was seen.
    [Fact]
    public async Task AnEntryForAnotherHostGivesNoDateWithAResponse()
    {
        SaveRejectedEntry(OtherHost);

        ProbeResult result = await CheckAsync(Answering(HttpStatusCode.OK), HttpsUrl);

        Assert.Equal(ServiceStatus.Up, result.Status);
        AssertNoNewHandshake(result);
    }

    [Fact]
    public async Task ARejectedEntryForAnotherHostIsNotReadWithATlsFailure()
    {
        SaveRejectedEntry(OtherHost);

        ProbeResult result = await CheckAsync(FailingWith(TlsFailure()), HttpsUrl);

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Null(result.Certificate);
    }

    [Theory]
    [InlineData(HttpHealthProbe.CertificateNotValidReason)]
    [InlineData(HttpHealthProbe.NoNewHandshakeReason)]
    [InlineData(SecurityHeaderReader.NotHttpsReason)]
    public void EveryReasonIsAShortFixedText(string reason)
    {
        Assert.True(reason.Length <= 64);
    }

    private async Task AssertEntryIsGoneAfterAsync(ScriptedHttpHandler handler)
    {
        SaveAcceptedEntry(Host);

        await CheckAsync(handler, HttpsUrl);

        Assert.Null(_recorder.Take(Host));
    }

    private static void AssertNoNewHandshake(ProbeResult result)
    {
        Assert.NotNull(result.Certificate);
        Assert.Null(result.Certificate.NotAfterUtc);
        Assert.Equal(HttpHealthProbe.NoNewHandshakeReason, result.Certificate.NotAssessedReason);
    }

    private void SaveAcceptedEntry(string host)
    {
        _recorder.RecordHandshake(host, _certificate, true);
    }

    private void SaveRejectedEntry(string host)
    {
        _recorder.RecordHandshake(host, _certificate, false);
    }

    private static ScriptedHttpHandler Answering(HttpStatusCode code)
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.Response = new HttpResponseMessage(code);
        return handler;
    }

    private static ScriptedHttpHandler FailingWith(Exception failure)
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.Failure = failure;
        return handler;
    }

    private static HttpRequestException TlsFailure()
    {
        return new HttpRequestException("handshake failed", new AuthenticationException("validation failed"));
    }

    private static HttpRequestException ConnectionFailure()
    {
        return new HttpRequestException("connection failed");
    }

    private async Task<ProbeResult> CheckAsync(ScriptedHttpHandler handler, string url)
    {
        using HttpClient client = new HttpClient(handler);
        HttpHealthProbe probe = new HttpHealthProbe(client, _recorder);
        return await probe.CheckAsync(url, 1, CancellationToken.None);
    }
}
