using System.Net;
using DeadSwitch.Api.Monitoring;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DeadSwitch.Tests;

// The header warnings, through the real API, the real database and a fake probe.
public class SecurityWarningFlowTests
{
    private const string Frame = TestSecurityHeaders.FrameOptions;
    private const string Referrer = TestSecurityHeaders.ReferrerPolicy;
    private const string Nothing = TestProbeResults.NothingMissing;

    [Fact]
    public async Task FirstAssessedCheckGivesNoWarningEvenWithHeadersMissing()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();

        await scenario.CheckAsync(TestProbeResults.Assessed(Frame + ", " + Referrer));

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task HeaderThatDisappearsGivesOneWarningWithItsName()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));

        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(SecurityWarningKind.HeadersDisappeared, warning.Kind);
        Assert.Equal(SecurityWarningScenario.ServiceName, warning.ServiceName);
        Assert.Equal(new string[] { Frame }, warning.DisappearedHeaders);
        Assert.Null(warning.ThresholdDays);
    }

    [Fact]
    public async Task HeaderThatStaysMissingWarnsOnlyOnce()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));

        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));
        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));
        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task HeaderThatComesBackGivesNoWarning()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task HeaderThatDisappearsAgainWarnsAgain()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));

        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        Assert.Equal(2, scenario.GetWarnings().Count);
    }

    [Fact]
    public async Task WhenOneHeaderReturnsAndAnotherDisappearsOnlyTheLostOneIsNamed()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        await scenario.CheckAsync(TestProbeResults.Assessed(Referrer));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(new string[] { Referrer }, warning.DisappearedHeaders);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("connection failure")]
    [InlineData("blocked")]
    [InlineData("redirect")]
    [InlineData("client error")]
    [InlineData("server error")]
    public async Task ACheckWithoutAssessmentGivesNoWarning(string kind)
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));

        await scenario.CheckAsync(TestProbeResults.HeadersNotAssessedBy(kind));

        Assert.Empty(scenario.GetWarnings());
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("connection failure")]
    [InlineData("blocked")]
    [InlineData("redirect")]
    [InlineData("client error")]
    [InlineData("server error")]
    public async Task ACheckWithoutAssessmentDoesNotResetTheComparison(string kind)
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));

        await scenario.CheckAsync(TestProbeResults.HeadersNotAssessedBy(kind));
        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(new string[] { Frame }, warning.DisappearedHeaders);
    }

    [Fact]
    public async Task FirstAssessedCheckAfterOnlyTimeoutsGivesNoWarning()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Timeout());
        await scenario.CheckAsync(TestProbeResults.Timeout());

        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        Assert.Empty(scenario.GetWarnings());
    }

    // The pattern from the owner's run against a good site: 1 timeout of 34.
    [Fact]
    public async Task OneTimeoutIn34ChecksGivesNoSecurityWarningAndNoStatusMessage()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        const int TimeoutAtCheck = 17;
        DateTime notAfter = scenario.DateWithDaysLeft(90);

        for (int check = 0; check < 34; check++)
        {
            if (check == TimeoutAtCheck)
            {
                await scenario.CheckAsync(TestProbeResults.Timeout());
            }
            else
            {
                await scenario.CheckAsync(TestProbeResults.Response(200, TestProbeResults.HeadersAssessed(Nothing), TestProbeResults.CertificateValid(notAfter)));
            }
        }

        Assert.Empty(scenario.GetWarnings());

        // A single timeout is not confirmed as DOWN, so no DOWN and no UP message is sent.
        Assert.Empty(scenario.App.Notifier.GetChanges());
    }

    [Fact]
    public async Task NewUrlHasNoBaselineFromTheOldAddress()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        await scenario.ChangeUrlAsync(SecurityWarningScenario.OtherUrl);

        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task TimeoutAsTheFirstCheckOfANewUrlDoesNotBringBackTheOldAddress()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        await scenario.ChangeUrlAsync(SecurityWarningScenario.OtherUrl);

        await scenario.CheckAsync(TestProbeResults.Timeout());
        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task ChangingBackToAnEarlierUrlDoesNotCompareWithItsOldChecks()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        await scenario.ChangeUrlAsync(SecurityWarningScenario.OtherUrl);
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        await scenario.ChangeUrlAsync(SecurityWarningScenario.ServiceUrl);

        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task ChangingBackAndThenATimeoutStillDoesNotCompareWithTheOldChecks()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        await scenario.ChangeUrlAsync(SecurityWarningScenario.OtherUrl);
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        await scenario.ChangeUrlAsync(SecurityWarningScenario.ServiceUrl);

        await scenario.CheckAsync(TestProbeResults.Timeout());
        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        Assert.Empty(scenario.GetWarnings());
    }

    // The other URL has no assessed row, so only its timeout row marks where the current URL began.
    [Fact]
    public async Task ChangingBackAfterOnlyTimeoutsOnTheOtherUrlDoesNotCompareWithTheOldChecks()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        await scenario.ChangeUrlAsync(SecurityWarningScenario.OtherUrl);
        await scenario.CheckAsync(TestProbeResults.Timeout());
        await scenario.ChangeUrlAsync(SecurityWarningScenario.ServiceUrl);

        await scenario.CheckAsync(TestProbeResults.Timeout());
        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        Assert.Empty(scenario.GetWarnings());
    }

    // No check of the other URL exists to mark the change, so only LastCheckedAt shows that it happened.
    [Fact]
    public async Task ChangingToAnotherUrlAndBackWithoutACheckStartsOver()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        await scenario.ChangeUrlAsync(SecurityWarningScenario.OtherUrl);
        await scenario.ChangeUrlAsync(SecurityWarningScenario.ServiceUrl);

        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task NewNameKeepsTheComparisonAndTheWarningUsesTheNewName()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        await scenario.ChangeNameAsync("New name");

        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal("New name", warning.ServiceName);
    }

    [Fact]
    public async Task OnlyNamesFromTheFixedListCanReachAWarning()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));

        await scenario.CheckAsync(TestProbeResults.Assessed("<@1> planted text, " + Frame));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(new string[] { Frame }, warning.DisappearedHeaders);
    }

    [Fact]
    public async Task WorkerGivesTheSameWarningAsAManualCheck()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        using MonitoringWorker worker = scenario.App.CreateWorker();
        await scenario.RunWorkerPassAsync(worker, TestProbeResults.Assessed(Nothing));

        await scenario.RunWorkerPassAsync(worker, TestProbeResults.Assessed(Frame));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(new string[] { Frame }, warning.DisappearedHeaders);
    }

    [Fact]
    public async Task OlderCompatibilityRouteAlsoWarns()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        scenario.App.Clock.Advance(TimeSpan.FromSeconds(1));
        scenario.App.Probe.Result = TestProbeResults.Assessed(Frame);

        HttpResponseMessage response = await scenario.Client.GetAsync($"/services/{scenario.Service.Id}/check");

        response.EnsureSuccessStatusCode();
        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task CheckingAnAddressWithoutSavingNeverWarns()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        scenario.App.Probe.Result = TestProbeResults.Assessed(Frame);

        HttpResponseMessage response = await scenario.Client.GetAsync("/check?url=" + SecurityWarningScenario.ServiceUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(scenario.GetWarnings());
    }

    // A new worker has no memory of its own, so it can only know this from the database.
    [Fact]
    public async Task RestartedWorkerDoesNotWarnAgain()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        using MonitoringWorker worker = scenario.App.CreateWorker();
        await scenario.RunWorkerPassAsync(worker, TestProbeResults.Assessed(Nothing));
        await scenario.RunWorkerPassAsync(worker, TestProbeResults.Assessed(Frame));

        using MonitoringWorker restartedWorker = scenario.App.CreateWorker();
        await scenario.RunWorkerPassAsync(restartedWorker, TestProbeResults.Assessed(Frame));

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task HeaderLostWhileTheWorkerWasDownWarnsOnceAfterTheRestart()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        using MonitoringWorker worker = scenario.App.CreateWorker();
        await scenario.RunWorkerPassAsync(worker, TestProbeResults.Assessed(Nothing));

        using MonitoringWorker restartedWorker = scenario.App.CreateWorker();
        await scenario.RunWorkerPassAsync(restartedWorker, TestProbeResults.Assessed(Frame));
        await scenario.RunWorkerPassAsync(restartedWorker, TestProbeResults.Assessed(Frame));

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task StatusMessageIsSentBeforeTheWarnings()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));

        // Two timeouts, because one is not enough for a DOWN message.
        await scenario.CheckAsync(TestProbeResults.Timeout());
        await scenario.CheckAsync(TestProbeResults.Timeout());

        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        List<string> expected = new List<string> { TestNotifier.StatusSent, TestNotifier.StatusSent, TestNotifier.WarningSent };
        Assert.Equal(expected, scenario.App.Notifier.GetSendOrder());
    }

    [Fact]
    public async Task CheckIsSavedAndTheLockReleasedWhenAWarningIsSent()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        ServiceLocks locks = scenario.App.Services.GetRequiredService<ServiceLocks>();
        int savedChecksWhenWarned = 0;
        int locksWhenWarned = -1;

        async Task RememberTheState()
        {
            savedChecksWhenWarned = await scenario.CountHistoryAsync();
            locksWhenWarned = locks.ActiveCount;
        }

        scenario.App.Notifier.RunWhenNotified = RememberTheState;
        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        Assert.Single(scenario.GetWarnings());
        Assert.Equal(2, savedChecksWhenWarned);
        Assert.Equal(0, locksWhenWarned);
    }
}
