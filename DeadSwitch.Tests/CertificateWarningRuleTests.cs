using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

public class CertificateWarningRuleTests
{
    private static readonly DateTime ADate = new DateTime(2027, 1, 27, 23, 59, 59, DateTimeKind.Utc);

    [Fact]
    public void ADateMeansValid()
    {
        Assert.Equal(CertificateFinding.Valid, CertificateWarningRule.Classify(ADate, null));
    }

    [Fact]
    public void TheRejectedTextMeansRejected()
    {
        Assert.Equal(CertificateFinding.Rejected, CertificateWarningRule.Classify(null, CertificateReasons.NotValid));
    }

    [Fact]
    public void TheHandshakeTextMeansNotSeen()
    {
        Assert.Equal(CertificateFinding.NotSeen, CertificateWarningRule.Classify(null, CertificateReasons.NoNewHandshake));
    }

    [Fact]
    public void NoDateAndNoReasonMeansNotLookedAt()
    {
        Assert.Equal(CertificateFinding.NotLookedAt, CertificateWarningRule.Classify(null, null));
    }

    [Theory]
    [InlineData("Not an HTTPS address.")]
    [InlineData(TestProbeResults.UnknownCertificateReason)]
    [InlineData("")]
    [InlineData("the certificate was not valid.")]
    [InlineData("The certificate was not valid")]
    [InlineData("The certificate was not valid. ")]
    [InlineData("no new tls handshake for this check.")]
    public void EveryOtherTextMeansNotLookedAt(string reason)
    {
        Assert.Equal(CertificateFinding.NotLookedAt, CertificateWarningRule.Classify(null, reason));
    }

    [Fact]
    public void ADateWinsOverAReason()
    {
        Assert.Equal(CertificateFinding.Valid, CertificateWarningRule.Classify(ADate, CertificateReasons.NotValid));
    }

    [Fact]
    public void TheSavedTextsAreExactlyTheOnesAlreadyInTheDatabase()
    {
        Assert.Equal("The certificate was not valid.", CertificateReasons.NotValid);
        Assert.Equal("No new TLS handshake for this check.", CertificateReasons.NoNewHandshake);
    }

    [Theory]
    [InlineData(15, 14, null)]
    [InlineData(14, 14, 14)]
    [InlineData(8, 14, 14)]
    [InlineData(7, 14, 7)]
    [InlineData(2, 14, 7)]
    [InlineData(1, 14, 1)]
    [InlineData(0, 14, 1)]
    [InlineData(-1, 14, 1)]
    [InlineData(6, 5, null)]
    [InlineData(5, 5, 5)]
    [InlineData(2, 5, 5)]
    [InlineData(1, 5, 1)]
    [InlineData(8, 7, null)]
    [InlineData(7, 7, 7)]
    [InlineData(2, 7, 7)]
    [InlineData(1, 7, 1)]
    [InlineData(2, 1, null)]
    [InlineData(1, 1, 1)]
    [InlineData(0, 1, 1)]
    [InlineData(31, 30, null)]
    [InlineData(30, 30, 30)]
    [InlineData(8, 30, 30)]
    [InlineData(7, 30, 7)]
    public void TheThresholdFollowsTheDaysLeftAndTheSetting(int daysLeft, int warningDays, int? expected)
    {
        Assert.Equal(expected, CertificateWarningRule.ThresholdFor(daysLeft, warningDays));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(14, null, true)]
    [InlineData(1, null, true)]
    [InlineData(7, 14, true)]
    [InlineData(1, 7, true)]
    [InlineData(1, 14, true)]
    [InlineData(14, 14, false)]
    [InlineData(7, 7, false)]
    [InlineData(14, 7, false)]
    [InlineData(null, 7, false)]
    public void OnlyAWorseThresholdCounts(int? current, int? previous, bool expected)
    {
        Assert.Equal(expected, CertificateWarningRule.IsWorseThreshold(current, previous));
    }

    [Theory]
    [InlineData(CertificateFinding.Valid, CertificateFinding.Rejected, true)]
    [InlineData(CertificateFinding.Rejected, CertificateFinding.Rejected, false)]
    [InlineData(null, CertificateFinding.Rejected, false)]
    [InlineData(CertificateFinding.Valid, CertificateFinding.Valid, false)]
    [InlineData(CertificateFinding.Valid, CertificateFinding.NotSeen, false)]
    [InlineData(CertificateFinding.Valid, CertificateFinding.NotLookedAt, false)]
    public void OnlyValidToRejectedIsNewlyRejected(CertificateFinding? baseline, CertificateFinding current, bool expected)
    {
        Assert.Equal(expected, CertificateWarningRule.IsNewlyRejected(baseline, current));
    }
}
