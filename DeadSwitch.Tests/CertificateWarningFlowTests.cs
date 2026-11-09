using DeadSwitch.Api.Monitoring;
using DeadSwitch.Application.Interfaces;

namespace DeadSwitch.Tests;

// The certificate warnings, through the real API, the real database and a fake probe.
public class CertificateWarningFlowTests
{
    private const string Frame = TestSecurityHeaders.FrameOptions;
    private const string Nothing = TestProbeResults.NothingMissing;
    private const int FarAway = 90;

    [Fact]
    public async Task RejectedOnTheFirstCheckGivesNoWarning()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();

        await scenario.CheckAsync(TestProbeResults.Rejected());

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task ValidThenRejectedGivesOneWarning()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));

        await scenario.CheckAsync(TestProbeResults.Rejected());

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(SecurityWarningKind.CertificateRejected, warning.Kind);
        Assert.Empty(warning.DisappearedHeaders);
        Assert.Null(warning.ThresholdDays);
    }

    [Fact]
    public async Task RejectedAgainWarnsOnlyOnce()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));

        await scenario.CheckAsync(TestProbeResults.Rejected());
        await scenario.CheckAsync(TestProbeResults.Rejected());

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task TimeoutIsNotARejectedCertificate()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));

        await scenario.CheckAsync(TestProbeResults.Timeout());

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task NotSeenIsNotARejectedCertificate()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));

        await scenario.CheckAsync(TestProbeResults.NotSeen());

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task UnknownReasonTextGivesNoWarning()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));

        await scenario.CheckAsync(TestProbeResults.UnknownCertificateText());

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task TimeoutBetweenValidAndRejectedDoesNotResetTheComparison()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));

        await scenario.CheckAsync(TestProbeResults.Timeout());
        await scenario.CheckAsync(TestProbeResults.Rejected());

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task NotSeenBetweenValidAndRejectedDoesNotResetTheComparison()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));

        await scenario.CheckAsync(TestProbeResults.NotSeen());
        await scenario.CheckAsync(TestProbeResults.Rejected());

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task UnknownReasonBetweenValidAndRejectedIsNotABaseline()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));

        await scenario.CheckAsync(TestProbeResults.UnknownCertificateText());
        await scenario.CheckAsync(TestProbeResults.Rejected());

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task UnknownReasonBetweenTwoRejectedDoesNotWarnAgain()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));
        await scenario.CheckAsync(TestProbeResults.Rejected());

        await scenario.CheckAsync(TestProbeResults.UnknownCertificateText());
        await scenario.CheckAsync(TestProbeResults.Rejected());

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task TimeoutBetweenTwoRejectedDoesNotWarnAgain()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));
        await scenario.CheckAsync(TestProbeResults.Rejected());

        await scenario.CheckAsync(TestProbeResults.Timeout());
        await scenario.CheckAsync(TestProbeResults.Rejected());

        Assert.Single(scenario.GetWarnings());
    }

    // The pattern from the owner's run against expired.badssl.com: 4 timeouts of 14.
    [Fact]
    public async Task FourTimeoutsIn14ChecksOfARejectedCertificateWarnOnlyOnce()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));
        List<int> timeoutAtChecks = new List<int> { 2, 5, 6, 10 };

        for (int check = 0; check < 14; check++)
        {
            if (timeoutAtChecks.Contains(check))
            {
                await scenario.CheckAsync(TestProbeResults.Timeout());
            }
            else
            {
                await scenario.CheckAsync(TestProbeResults.Rejected());
            }
        }

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task RejectedThenValidAndRejectedAgainWarnsAtEveryChange()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));

        await scenario.CheckAsync(TestProbeResults.Rejected());
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));
        await scenario.CheckAsync(TestProbeResults.Rejected());

        Assert.Equal(2, scenario.GetWarnings().Count);
    }

    [Fact]
    public async Task FirstCheckWithFewDaysLeftWarnsOnce()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();

        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(5));
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(5));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(SecurityWarningKind.CertificateExpiringSoon, warning.Kind);
        Assert.Equal(7, warning.ThresholdDays);
        Assert.Empty(warning.DisappearedHeaders);
    }

    [Fact]
    public async Task FirstCheckWithManyDaysLeftGivesNoWarning()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();

        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task EachThresholdWarnsExactlyOnceAsTheDaysGoBy()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        DateTime notAfter = scenario.DateWithDaysLeft(16);
        ProbeResult sameCertificate = TestProbeResults.Response(200, null, TestProbeResults.CertificateValid(notAfter));
        await scenario.CheckAsync(sameCertificate);
        Assert.Empty(scenario.GetWarnings());

        await CheckAfterDaysAsync(scenario, 2, sameCertificate);
        await CheckAfterDaysAsync(scenario, 1, sameCertificate);
        await CheckAfterDaysAsync(scenario, 6, sameCertificate);
        await CheckAfterDaysAsync(scenario, 1, sameCertificate);
        await CheckAfterDaysAsync(scenario, 5, sameCertificate);
        await CheckAfterDaysAsync(scenario, 1, sameCertificate);

        List<SecurityWarning> warnings = scenario.GetWarnings();
        Assert.Equal(3, warnings.Count);
        Assert.Equal(14, warnings[0].ThresholdDays);
        Assert.Equal(7, warnings[1].ThresholdDays);
        Assert.Equal(1, warnings[2].ThresholdDays);
    }

    [Fact]
    public async Task JumpOverSeveralThresholdsGivesOneWarningAtTheWorstOne()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));

        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(1));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(1, warning.ThresholdDays);
    }

    // A timeout must not hide a threshold that was passed while the site did not answer.
    [Fact]
    public async Task TimeoutDoesNotHideAThresholdThatWasPassed()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        DateTime notAfter = scenario.DateWithDaysLeft(16);
        ProbeResult sameCertificate = TestProbeResults.Response(200, null, TestProbeResults.CertificateValid(notAfter));
        await scenario.CheckAsync(sameCertificate);

        scenario.App.Clock.Advance(TimeSpan.FromDays(2));
        await scenario.CheckAsync(TestProbeResults.Timeout());
        await scenario.CheckAsync(sameCertificate);

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(14, warning.ThresholdDays);
    }

    [Fact]
    public async Task TimeoutBetweenTwoChecksInTheSameThresholdDoesNotWarnAgain()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(5));

        await scenario.CheckAsync(TestProbeResults.Timeout());
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(5));

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task RenewedCertificateStartsOver()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(5));

        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));
        Assert.Single(scenario.GetWarnings());

        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(5));
        Assert.Equal(2, scenario.GetWarnings().Count);
    }

    [Fact]
    public async Task ValidWithFewDaysLeftAfterRejectedWarns()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Rejected());

        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(5));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(SecurityWarningKind.CertificateExpiringSoon, warning.Kind);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task ValidCertificateIsJudgedOnAnAnswerWhereTheHeadersAreNot(int statusCode)
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Response(200, TestProbeResults.HeadersAssessed(Nothing), TestProbeResults.CertificateValid(scenario.DateWithDaysLeft(FarAway))));

        await scenario.CheckAsync(TestProbeResults.Response(statusCode, TestProbeResults.HeadersNotAssessed(), TestProbeResults.CertificateValid(scenario.DateWithDaysLeft(5))));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(SecurityWarningKind.CertificateExpiringSoon, warning.Kind);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task SuchAnAnswerMovesTheCertificateBaselineButNotTheHeaderBaseline(int statusCode)
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Response(200, TestProbeResults.HeadersAssessed(Nothing), TestProbeResults.CertificateValid(scenario.DateWithDaysLeft(FarAway))));
        await scenario.CheckAsync(TestProbeResults.Response(statusCode, TestProbeResults.HeadersNotAssessed(), TestProbeResults.CertificateValid(scenario.DateWithDaysLeft(5))));

        await scenario.CheckAsync(TestProbeResults.Response(200, TestProbeResults.HeadersAssessed(Frame), TestProbeResults.CertificateValid(scenario.DateWithDaysLeft(5))));

        List<SecurityWarning> warnings = scenario.GetWarnings();
        Assert.Equal(2, warnings.Count);
        Assert.Equal(SecurityWarningKind.CertificateExpiringSoon, warnings[0].Kind);
        Assert.Equal(SecurityWarningKind.HeadersDisappeared, warnings[1].Kind);
    }

    [Fact]
    public async Task HeadersAreJudgedOnAnAnswerWhereTheCertificateIsNot()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));

        await scenario.CheckAsync(TestProbeResults.Response(200, TestProbeResults.HeadersAssessed(Frame), TestProbeResults.CertificateWithReason(DeadSwitch.Domain.CertificateReasons.NoNewHandshake)));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(SecurityWarningKind.HeadersDisappeared, warning.Kind);
    }

    [Fact]
    public async Task HeadersAndCertificateCanBothWarnFromTheSameCheck()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Response(200, TestProbeResults.HeadersAssessed(Nothing), TestProbeResults.CertificateValid(scenario.DateWithDaysLeft(FarAway))));

        await scenario.CheckAsync(TestProbeResults.Response(200, TestProbeResults.HeadersAssessed(Frame), TestProbeResults.CertificateValid(scenario.DateWithDaysLeft(5))));

        Assert.Equal(2, scenario.GetWarnings().Count);
    }

    [Fact]
    public async Task NewUrlHasNoCertificateBaselineFromTheOldAddress()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));
        await scenario.ChangeUrlAsync(SecurityWarningScenario.OtherUrl);

        await scenario.CheckAsync(TestProbeResults.Rejected());

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task ChangingBackToAnEarlierUrlDoesNotUseItsOldCertificate()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));
        await scenario.ChangeUrlAsync(SecurityWarningScenario.OtherUrl);
        await scenario.CheckAsync(TestProbeResults.Timeout());
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));
        await scenario.ChangeUrlAsync(SecurityWarningScenario.ServiceUrl);

        await scenario.CheckAsync(TestProbeResults.Timeout());
        await scenario.CheckAsync(TestProbeResults.Rejected());

        Assert.Empty(scenario.GetWarnings());
    }

    // The other URL has no verdict, so only its timeout row marks where the current URL began.
    [Fact]
    public async Task ChangingBackAfterOnlyTimeoutsOnTheOtherUrlDoesNotUseTheOldCertificate()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));
        await scenario.ChangeUrlAsync(SecurityWarningScenario.OtherUrl);
        await scenario.CheckAsync(TestProbeResults.Timeout());
        await scenario.ChangeUrlAsync(SecurityWarningScenario.ServiceUrl);

        await scenario.CheckAsync(TestProbeResults.Timeout());
        await scenario.CheckAsync(TestProbeResults.Rejected());

        Assert.Empty(scenario.GetWarnings());
    }

    // No check of the other URL exists to mark the change, so only LastCheckedAt shows that it happened.
    [Fact]
    public async Task ChangingToAnotherUrlAndBackWithoutACheckStartsTheCertificateOver()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(FarAway));
        await scenario.ChangeUrlAsync(SecurityWarningScenario.OtherUrl);
        await scenario.ChangeUrlAsync(SecurityWarningScenario.ServiceUrl);

        await scenario.CheckAsync(TestProbeResults.Rejected());

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task RestartedWorkerDoesNotRepeatARejectedWarning()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        using MonitoringWorker worker = scenario.App.CreateWorker();
        await scenario.RunWorkerPassAsync(worker, scenario.ValidCertificateWithDaysLeft(FarAway));
        await scenario.RunWorkerPassAsync(worker, TestProbeResults.Rejected());

        using MonitoringWorker restartedWorker = scenario.App.CreateWorker();
        await scenario.RunWorkerPassAsync(restartedWorker, TestProbeResults.Rejected());

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task RestartedWorkerDoesNotRepeatAThresholdWarning()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        using MonitoringWorker worker = scenario.App.CreateWorker();
        await scenario.RunWorkerPassAsync(worker, scenario.ValidCertificateWithDaysLeft(5));

        using MonitoringWorker restartedWorker = scenario.App.CreateWorker();
        await scenario.RunWorkerPassAsync(restartedWorker, scenario.ValidCertificateWithDaysLeft(5));

        Assert.Single(scenario.GetWarnings());
    }

    private static async Task CheckAfterDaysAsync(SecurityWarningScenario scenario, int days, ProbeResult result)
    {
        scenario.App.Clock.Advance(TimeSpan.FromDays(days));
        await scenario.CheckAsync(result);
    }
}
