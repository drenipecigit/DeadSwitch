using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

public class CertificateExpiryRuleTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-2 * TimeSpan.TicksPerDay, -2)]
    [InlineData(-TimeSpan.TicksPerDay, -1)]
    [InlineData(-1L, -1)]
    [InlineData(0L, 0)]
    [InlineData(1L, 0)]
    [InlineData(TimeSpan.TicksPerDay - 1, 0)]
    [InlineData(TimeSpan.TicksPerDay, 1)]
    [InlineData(2 * TimeSpan.TicksPerDay - 1, 1)]
    [InlineData(14 * TimeSpan.TicksPerDay, 14)]
    public void DaysLeftCountsWholeDaysRoundedDown(long ticksLeft, int expected)
    {
        DateTime notAfter = Now.UtcDateTime.AddTicks(ticksLeft);

        int daysLeft = CertificateExpiryRule.DaysLeft(notAfter, Now);

        Assert.Equal(expected, daysLeft);
    }

    [Theory]
    [InlineData(-100, "Expired")]
    [InlineData(-1, "Expired")]
    [InlineData(0, "Expires today")]
    [InlineData(1, "1 day left")]
    [InlineData(2, "2 days left")]
    [InlineData(14, "14 days left")]
    public void TheTextFollowsTheDaysLeft(int daysLeft, string expected)
    {
        Assert.Equal(expected, CertificateExpiryRule.TextFor(daysLeft));
    }
}
