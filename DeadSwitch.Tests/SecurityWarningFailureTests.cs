using System.Net;
using DeadSwitch.Api.Monitoring;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeadSwitch.Tests;

public class SecurityWarningFailureTests
{
    private const string Frame = TestSecurityHeaders.FrameOptions;
    private const string Nothing = TestProbeResults.NothingMissing;
    private const string ExpectedFailureText = "The channel did not answer.";
    private const string SecretText = "text-that-must-never-be-logged";

    [Fact]
    public async Task ExpectedFailureKeepsTheCheckAndIsLoggedAsAWarning()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        scenario.App.Notifier.ExceptionToThrow = new NotificationFailedException(ExpectedFailureText);

        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        Assert.Equal(2, await scenario.CountHistoryAsync());
        Assert.True(scenario.App.Logs.Contains(LogLevel.Warning, ExpectedFailureText));
    }

    // Only the type may reach the log, because the text of an unexpected exception can hold anything.
    [Fact]
    public async Task UnexpectedFailureKeepsTheCheckAndLogsOnlyTheType()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        scenario.App.Notifier.ExceptionToThrow = new InvalidOperationException(SecretText);

        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        Assert.Equal(2, await scenario.CountHistoryAsync());
        Assert.True(scenario.App.Logs.Contains(LogLevel.Error, nameof(InvalidOperationException)));
        scenario.App.Logs.AssertNothingContains(SecretText);
    }

    [Fact]
    public async Task OneFailedWarningDoesNotStopTheNext()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(BothGood(scenario));
        scenario.App.Notifier.SecurityWarningFailuresLeft = 1;

        await scenario.CheckAsync(BothWorse(scenario));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(SecurityWarningKind.CertificateExpiringSoon, warning.Kind);
    }

    [Fact]
    public async Task AFailedStatusMessageDoesNotStopTheWarnings()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        await scenario.CheckAsync(TestProbeResults.Timeout());
        scenario.App.Notifier.StatusFailuresLeft = 1;

        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        Assert.Single(scenario.GetWarnings());
    }

    [Fact]
    public async Task ALostWarningIsNotSentAgainByTheNextCheck()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        scenario.App.Notifier.SecurityWarningFailuresLeft = 1;
        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        await scenario.CheckAsync(TestProbeResults.Assessed(Frame));

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task WorkerGoesOnToTheNextServiceWhenAWarningFails()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.App.RegisterServiceAsync(scenario.Client, new ServiceConfiguration("Zeta", SecurityWarningScenario.OtherUrl));
        using MonitoringWorker worker = scenario.App.CreateWorker();
        await scenario.RunWorkerPassAsync(worker, TestProbeResults.Assessed(Nothing));
        scenario.App.Notifier.ExceptionToThrow = new InvalidOperationException(SecretText);
        int callsBefore = scenario.App.Probe.Calls;

        await scenario.RunWorkerPassAsync(worker, TestProbeResults.Assessed(Frame));

        Assert.Equal(callsBefore + 2, scenario.App.Probe.Calls);
    }

    [Fact]
    public async Task DatabaseFailureAtTheBaselineSavesNothingAndSendsNothing()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(Nothing));
        scenario.App.RepositoryControl.ExceptionFromBaseline = new InvalidOperationException("The test made the read fail.");
        scenario.App.Probe.Result = TestProbeResults.Assessed(Frame);

        HttpResponseMessage response = await scenario.Client.PostAsync($"/services/{scenario.Service.Id}/check", null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(scenario.GetWarnings());
        Assert.Equal(1, await scenario.CountHistoryAsync());
        Assert.Equal(0, scenario.App.Services.GetRequiredService<ServiceLocks>().ActiveCount);
    }

    // A shutdown before the save: the same as a cancelled check today, nothing is saved or sent.
    [Fact]
    public async Task ShutdownWhileReadingABaselineSavesNothingAndSendsNothing()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        using MonitoringWorker worker = scenario.App.CreateWorker();
        await scenario.RunWorkerPassAsync(worker, TestProbeResults.Assessed(Nothing));
        using CancellationTokenSource shutdown = new CancellationTokenSource();
        scenario.App.RepositoryControl.RunAtBaseline = shutdown.Cancel;
        scenario.App.Clock.Advance(TimeSpan.FromSeconds(61));
        scenario.App.Probe.Result = TestProbeResults.Assessed(Frame);

        Task pass = worker.RunPassAsync(shutdown.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);
        scenario.App.RepositoryControl.RunAtBaseline = null;
        Assert.Empty(scenario.GetWarnings());
        Assert.Equal(1, await scenario.CountHistoryAsync());
        Assert.Equal(0, scenario.App.Services.GetRequiredService<ServiceLocks>().ActiveCount);
    }

    // A shutdown after the save must not lose a warning: the next check sees no change to warn about.
    [Fact]
    public async Task ShutdownWhileSendingStillSendsEveryWarningOfASavedCheck()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        using MonitoringWorker worker = scenario.App.CreateWorker();
        await scenario.RunWorkerPassAsync(worker, BothGood(scenario));
        using CancellationTokenSource shutdown = new CancellationTokenSource();

        Task CancelTheShutdownToken()
        {
            shutdown.Cancel();
            return Task.CompletedTask;
        }

        scenario.App.Notifier.RunWhenNotified = CancelTheShutdownToken;
        scenario.App.Clock.Advance(TimeSpan.FromSeconds(61));
        scenario.App.Probe.Result = BothWorse(scenario);

        await worker.RunPassAsync(shutdown.Token);

        Assert.Equal(2, scenario.GetWarnings().Count);
        Assert.Equal(2, await scenario.CountHistoryAsync());
    }

    // Nothing in the sending uses the worker's token, so the catch around it cannot hide a shutdown.
    [Fact]
    public async Task FailedSendingDuringAShutdownIsLoggedAndDoesNotFailThePass()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        using MonitoringWorker worker = scenario.App.CreateWorker();
        await scenario.RunWorkerPassAsync(worker, BothGood(scenario));
        using CancellationTokenSource shutdown = new CancellationTokenSource();

        Task CancelTheShutdownToken()
        {
            shutdown.Cancel();
            return Task.CompletedTask;
        }

        scenario.App.Notifier.RunWhenNotified = CancelTheShutdownToken;
        scenario.App.Notifier.ExceptionToThrow = new NotificationFailedException(ExpectedFailureText);
        scenario.App.Clock.Advance(TimeSpan.FromSeconds(61));
        scenario.App.Probe.Result = BothWorse(scenario);

        await worker.RunPassAsync(shutdown.Token);

        Assert.Equal(2, await scenario.CountHistoryAsync());
        Assert.True(scenario.App.Logs.Contains(LogLevel.Warning, ExpectedFailureText));
    }

    private static ProbeResult BothGood(SecurityWarningScenario scenario)
    {
        return TestProbeResults.Response(200, TestProbeResults.HeadersAssessed(Nothing), TestProbeResults.CertificateValid(scenario.DateWithDaysLeft(90)));
    }

    private static ProbeResult BothWorse(SecurityWarningScenario scenario)
    {
        return TestProbeResults.Response(200, TestProbeResults.HeadersAssessed(Frame), TestProbeResults.CertificateValid(scenario.DateWithDaysLeft(5)));
    }
}
