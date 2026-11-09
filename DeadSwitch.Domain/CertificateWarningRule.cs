namespace DeadSwitch.Domain;

// Decides what a saved check says about the certificate, and when that is worth a warning.
// Here it can be tested without a database.
public static class CertificateWarningRule
{
    private const int SecondThresholdDays = 7;
    private const int LastThresholdDays = 1;

    // The reasons are compared exactly. A text that is not known must never be read as a verdict,
    // or a future reason could give a false alarm. The baseline query in the repository must agree
    // with this method, and CertificateBaselineQueryTests is what keeps them together.
    public static CertificateFinding Classify(DateTime? notAfterUtc, string? notAssessedReason)
    {
        if (notAfterUtc.HasValue)
        {
            return CertificateFinding.Valid;
        }

        if (notAssessedReason == CertificateReasons.NotValid)
        {
            return CertificateFinding.Rejected;
        }

        if (notAssessedReason == CertificateReasons.NoNewHandshake)
        {
            return CertificateFinding.NotSeen;
        }

        return CertificateFinding.NotLookedAt;
    }

    // The threshold the days left have reached, or null when none has been reached.
    // A lower number is more urgent. The fixed thresholds only apply below the configured one.
    public static int? ThresholdFor(int daysLeft, int warningDays)
    {
        if (daysLeft > warningDays)
        {
            return null;
        }

        if (daysLeft <= LastThresholdDays)
        {
            return LastThresholdDays;
        }

        if (daysLeft <= SecondThresholdDays && warningDays > SecondThresholdDays)
        {
            return SecondThresholdDays;
        }

        return warningDays;
    }

    public static bool IsWorseThreshold(int? current, int? previous)
    {
        if (current == null)
        {
            return false;
        }

        if (previous == null)
        {
            return true;
        }

        return current < previous;
    }

    // Only a certificate that was accepted before can get worse. A first check that is rejected has
    // nothing to be compared with, and it already gets a DOWN message.
    public static bool IsNewlyRejected(CertificateFinding? baseline, CertificateFinding current)
    {
        return baseline == CertificateFinding.Valid && current == CertificateFinding.Rejected;
    }
}
