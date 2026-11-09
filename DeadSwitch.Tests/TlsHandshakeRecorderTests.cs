using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DeadSwitch.Infrastructure.Monitoring;
using Microsoft.Extensions.Time.Testing;

namespace DeadSwitch.Tests;

// The recorder on its own. HttpsConnectionTests shows the same code during real TLS handshakes.
public class TlsHandshakeRecorderTests
{
    private const string Host = "monitored.test";
    private const string OtherHost = "other.test";

    private readonly FakeTimeProvider _clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    // Every combination of the three flags, because anything but "no errors at all" must be refused.
    [Theory]
    [InlineData(SslPolicyErrors.None, true)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateChainErrors, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, false)]
    public void TheAnswerIsTheSameAsTheFrameworksOwnRule(SslPolicyErrors errors, bool expected)
    {
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);

        bool answer = recorder.OnRemoteCertificate(new object(), null, null, errors);

        Assert.Equal(expected, answer);
    }

    [Fact]
    public void AnAcceptedCertificateKeepsItsExpiryInUtc()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);

        recorder.RecordHandshake(Host, certificate, true);
        TlsHandshake? handshake = recorder.Take(Host);

        Assert.NotNull(handshake);
        Assert.False(handshake.IsRejected);
        Assert.Equal(certificate.NotAfter.ToUniversalTime(), handshake.NotAfterUtc);
        Assert.Equal(DateTimeKind.Utc, handshake.NotAfterUtc!.Value.Kind);
    }

    [Fact]
    public void ARejectedCertificateKeepsNoDate()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);

        recorder.RecordHandshake(Host, certificate, false);
        TlsHandshake? handshake = recorder.Take(Host);

        Assert.NotNull(handshake);
        Assert.True(handshake.IsRejected);
        Assert.Null(handshake.NotAfterUtc);
    }

    [Fact]
    public void AMissingCertificateThatFailedValidationIsRejected()
    {
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);

        recorder.RecordHandshake(Host, null, false);
        TlsHandshake? handshake = recorder.Take(Host);

        Assert.NotNull(handshake);
        Assert.True(handshake.IsRejected);
    }

    // There is no date to keep, and this must not read as a rejected certificate.
    [Fact]
    public void AMissingCertificateThatPassedValidationLeavesNoEntry()
    {
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);

        recorder.RecordHandshake(Host, null, true);

        Assert.Null(recorder.Take(Host));
    }

    [Fact]
    public void TakeRemovesTheEntry()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);
        recorder.RecordHandshake(Host, certificate, true);

        recorder.Take(Host);

        Assert.Null(recorder.Take(Host));
    }

    [Fact]
    public void AnEntryForAnotherHostIsNotReturned()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);
        recorder.RecordHandshake(OtherHost, certificate, true);

        Assert.Null(recorder.Take(Host));
    }

    // Both directions, because a normalisation done on only one side would pass in one of them.
    [Theory]
    [InlineData("monitored.test.", "monitored.test")]
    [InlineData("monitored.test", "monitored.test.")]
    [InlineData("MONITORED.test", "monitored.test")]
    [InlineData("monitored.test", "MONITORED.test")]
    public void TheSameHostIsFoundWrittenAnotherWay(string savedAs, string takenAs)
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);
        recorder.RecordHandshake(savedAs, certificate, true);

        TlsHandshake? handshake = recorder.Take(takenAs);

        Assert.NotNull(handshake);
    }

    [Fact]
    public void AnEntryExactlyAtTheMaximumAgeIsUsed()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);
        recorder.RecordHandshake(Host, certificate, true);

        _clock.Advance(TimeSpan.FromMinutes(3));

        Assert.NotNull(recorder.Take(Host));
    }

    [Fact]
    public void AnEntryOlderThanTheMaximumAgeIsNotUsed()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);
        recorder.RecordHandshake(Host, certificate, true);

        _clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromTicks(1));

        Assert.Null(recorder.Take(Host));
    }

    [Fact]
    public void WhenFullANewHostIsNotSaved()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);
        FillRecorder(recorder, certificate);

        recorder.RecordHandshake(Host, certificate, true);

        Assert.Null(recorder.Take(Host));
    }

    [Fact]
    public void WhenFullAHostAlreadySavedIsStillUpdated()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);
        FillRecorder(recorder, certificate);

        recorder.RecordHandshake(HostNumber(0), certificate, false);
        TlsHandshake? handshake = recorder.Take(HostNumber(0));

        Assert.NotNull(handshake);
        Assert.True(handshake.IsRejected);
    }

    [Fact]
    public void WhenFullOldEntriesAreClearedToMakeRoom()
    {
        using X509Certificate2 certificate = TestCertificates.CreateForLocalAddress();
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(_clock);
        FillRecorder(recorder, certificate);
        _clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromTicks(1));

        recorder.RecordHandshake(Host, certificate, true);

        Assert.NotNull(recorder.Take(Host));
    }

    private static void FillRecorder(TlsHandshakeRecorder recorder, X509Certificate2 certificate)
    {
        for (int i = 0; i < TlsHandshakeRecorder.MaxEntries; i++)
        {
            recorder.RecordHandshake(HostNumber(i), certificate, true);
        }
    }

    private static string HostNumber(int number)
    {
        return $"host{number}.test";
    }
}
