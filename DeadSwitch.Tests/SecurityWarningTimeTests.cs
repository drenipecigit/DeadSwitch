using DeadSwitch.Application.Interfaces;

namespace DeadSwitch.Tests;

// Every kind of warning must carry the time its check started, because that time is shown in Discord.
// The test clock starts at 2026-11-03T12:00:00Z and every check in the scenario moves it one second.
public class SecurityWarningTimeTests
{
    private static readonly DateTimeOffset FirstCheck = new DateTimeOffset(2026, 11, 3, 12, 0, 1, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondCheck = new DateTimeOffset(2026, 11, 3, 12, 0, 2, TimeSpan.Zero);

    [Fact]
    public async Task AHeaderWarningCarriesTheTimeOfItsCheck()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(TestProbeResults.NothingMissing));

        await scenario.CheckAsync(TestProbeResults.Assessed(TestSecurityHeaders.FrameOptions));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(SecurityWarningKind.HeadersDisappeared, warning.Kind);
        Assert.Equal(SecondCheck, warning.CheckedAt);
    }

    [Fact]
    public async Task ARejectedCertificateWarningCarriesTheTimeOfItsCheck()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(90));

        await scenario.CheckAsync(TestProbeResults.Rejected());

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(SecurityWarningKind.CertificateRejected, warning.Kind);
        Assert.Equal(SecondCheck, warning.CheckedAt);
    }

    // A first check with few days left warns at once, so this warning comes from the first check.
    [Fact]
    public async Task AnExpiryWarningCarriesTheTimeOfItsCheck()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();

        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(5));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(SecurityWarningKind.CertificateExpiringSoon, warning.Kind);
        Assert.Equal(FirstCheck, warning.CheckedAt);
    }

    [Fact]
    public async Task ARedirectWarningCarriesTheTimeOfItsCheck()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());

        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost("other.example"));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(SecurityWarningKind.RedirectToOtherHost, warning.Kind);
        Assert.Equal(SecondCheck, warning.CheckedAt);
    }
}
