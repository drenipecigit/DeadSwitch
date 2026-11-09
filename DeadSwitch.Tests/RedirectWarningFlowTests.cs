using System.Net.Http.Json;
using DeadSwitch.Api.Monitoring;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

// The redirect warnings, through the real API, the real database and a fake probe.
public class RedirectWarningFlowTests
{
    private const string HostA = "first.example";
    private const string HostB = "second.example";

    // One second less than the limit, because CheckAsync moves the clock one second before the check.
    private static readonly TimeSpan JustInsideTheWindow = TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1);
    private static readonly TimeSpan JustOutsideTheWindow = TimeSpan.FromDays(30);

    [Fact]
    public async Task AFirstCheckToAnotherHostGivesNoWarning()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();

        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task ASiteThatStartsToRedirectElsewhereGivesOneWarningWithTheHost()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());

        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(SecurityWarningKind.RedirectToOtherHost, warning.Kind);
        Assert.Equal(SecurityWarningScenario.ServiceName, warning.ServiceName);
        Assert.Equal(HostA, warning.RedirectTargetHost);
    }

    [Fact]
    public async Task AChangeFromTheSameHostToAnotherHostWarns()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Redirect(RedirectFinding.SameHost, null));

        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task ARedirectWithinTheSameHostGivesNoWarning()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());

        await scenario.CheckAsync(TestProbeResults.Redirect(RedirectFinding.SameHost, null));

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task TheSameTargetWarnsOnlyOnce()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());

        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));
        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));
        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task ANewTargetWarnsAgain()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());

        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));
        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostB));

        List<SecurityWarning> warnings = scenario.GetWarnings();
        Assert.Equal(2, warnings.Count);
        Assert.Equal(HostA, warnings[0].RedirectTargetHost);
        Assert.Equal(HostB, warnings[1].RedirectTargetHost);
    }

    [Fact]
    public async Task SwitchingBetweenKnownTargetsDoesNotWarnAgain()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());

        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));
        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostB));
        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));
        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostB));

        Assert.Equal(2, scenario.GetWarnings().Count);
    }

    [Fact]
    public async Task ATargetThatComesBackWithinTheWindowDoesNotWarnAgain()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());
        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        await scenario.CheckAsync(TestProbeResults.NoRedirect());
        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task ATargetSeenExactlyAtTheLimitOfTheWindowDoesNotWarnAgain()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());
        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        scenario.App.Clock.Advance(JustInsideTheWindow);
        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task ATargetLastSeenOutsideTheWindowWarnsAgain()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());
        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        scenario.App.Clock.Advance(JustOutsideTheWindow);
        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        Assert.Equal(2, scenario.GetWarnings().Count);
    }

    // The window moves with every row: a target that stays is silent however long it stays.
    [Fact]
    public async Task ATargetThatStaysIsSilentForLongerThanTheWindow()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());
        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        for (int i = 0; i < 4; i++)
        {
            scenario.App.Clock.Advance(TimeSpan.FromDays(20));
            await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));
        }

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task AnInternalTargetGivesAWarningWithoutAHost()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());

        await scenario.CheckAsync(TestProbeResults.Redirect(RedirectFinding.InternalTarget, null));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(SecurityWarningKind.RedirectToInternalTarget, warning.Kind);
        Assert.Null(warning.RedirectTargetHost);
    }

    [Fact]
    public async Task AnUnreadableTargetGivesAWarningWithoutAHost()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());

        await scenario.CheckAsync(TestProbeResults.Redirect(RedirectFinding.Unreadable, null));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(SecurityWarningKind.RedirectUnreadable, warning.Kind);
        Assert.Null(warning.RedirectTargetHost);
    }

    // A known limitation, written down as a test: the category has no host, so a new value is the same.
    [Theory]
    [InlineData(RedirectFinding.Unreadable)]
    [InlineData(RedirectFinding.InternalTarget)]
    public async Task ACategoryWithoutAHostWarnsOnlyOnce(RedirectFinding finding)
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());

        await scenario.CheckAsync(TestProbeResults.Redirect(finding, null));
        await scenario.CheckAsync(TestProbeResults.Redirect(finding, null));

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task AFirstCheckToAnUnreadableTargetGivesNoWarning()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();

        await scenario.CheckAsync(TestProbeResults.Redirect(RedirectFinding.Unreadable, null));

        Assert.Empty(scenario.GetWarnings());
    }

    // A timeout learned nothing, so there is still nothing to compare with.
    [Fact]
    public async Task OnlyTimeoutsBeforeAreNotAFindingToCompareWith()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Timeout());

        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task ATimeoutBetweenChecksDoesNotHideANewTarget()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());
        await scenario.CheckAsync(TestProbeResults.Timeout());

        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task ANewUrlStartsOverAndGivesNoWarningAtItsFirstCheck()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());
        await scenario.ChangeUrlAsync(SecurityWarningScenario.OtherUrl);

        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        Assert.Empty(scenario.GetWarnings());
    }

    // The target was seen on the first URL, and that says nothing about the second one.
    [Fact]
    public async Task ATargetSeenOnAnotherUrlStillWarns()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());
        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));
        await scenario.ChangeUrlAsync(SecurityWarningScenario.OtherUrl);
        await scenario.CheckAsync(TestProbeResults.NoRedirect());

        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        Assert.Equal(2, scenario.GetWarnings().Count);
    }

    [Fact]
    public async Task ATargetSeenOnAnotherServiceStillWarns()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        MonitoredService other = await scenario.App.RegisterServiceAsync(scenario.Client, new ServiceConfiguration("Other", SecurityWarningScenario.ServiceUrl));
        await CheckServiceAsync(scenario, other.Id, TestProbeResults.RedirectToOtherHost(HostA));
        await scenario.CheckAsync(TestProbeResults.NoRedirect());

        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task ANewWorkerAgainstTheSameDatabaseDoesNotWarnAgain()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        MonitoringWorker firstWorker = scenario.App.CreateWorker();
        await scenario.RunWorkerPassAsync(firstWorker, TestProbeResults.NoRedirect());
        await scenario.RunWorkerPassAsync(firstWorker, TestProbeResults.RedirectToOtherHost(HostA));

        MonitoringWorker secondWorker = scenario.App.CreateWorker();
        await scenario.RunWorkerPassAsync(secondWorker, TestProbeResults.RedirectToOtherHost(HostA));

        Assert.Single(scenario.GetWarnings());
    }

    // A known limitation, written down as a test: the row is saved, so the target counts as seen.
    [Fact]
    public async Task AWarningThatFailsIsNotSentAgainForTheSameTarget()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.NoRedirect());
        scenario.App.Notifier.SecurityWarningFailuresLeft = 1;

        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));
        await scenario.CheckAsync(TestProbeResults.RedirectToOtherHost(HostA));

        Assert.Empty(scenario.GetWarnings());
        Assert.Equal(3, await scenario.CountHistoryAsync());
    }

    private static async Task CheckServiceAsync(SecurityWarningScenario scenario, Guid serviceId, ProbeResult result)
    {
        scenario.App.Probe.Result = result;
        HttpResponseMessage response = await scenario.Client.PostAsync($"/services/{serviceId}/check", null);
        response.EnsureSuccessStatusCode();
    }
}
