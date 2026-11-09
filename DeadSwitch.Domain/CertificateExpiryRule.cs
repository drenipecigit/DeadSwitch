namespace DeadSwitch.Domain;

public static class CertificateExpiryRule
{
    // Rounded down, so that a certificate with half a day left counts as 0 days. Saying too few days is
    // the safe mistake here. A date that has passed, even by a moment, gives a negative number.
    public static int DaysLeft(DateTime notAfterUtc, DateTimeOffset now)
    {
        TimeSpan timeLeft = notAfterUtc - now.UtcDateTime;
        return (int)Math.Floor(timeLeft.TotalDays);
    }

    // Written here and not in the page, because the limits can then be tested. No test runs JavaScript.
    public static string TextFor(int daysLeft)
    {
        if (daysLeft < 0)
        {
            return "Expired";
        }

        if (daysLeft == 0)
        {
            return "Expires today";
        }

        if (daysLeft == 1)
        {
            return "1 day left";
        }

        return $"{daysLeft} days left";
    }
}
