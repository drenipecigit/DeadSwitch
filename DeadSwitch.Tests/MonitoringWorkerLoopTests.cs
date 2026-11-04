using System.Net;
using System.Net.Http.Json;
using DeadSwitch.Api.Monitoring;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace DeadSwitch.Tests;

// These tests run the real loop in ExecuteAsync through StartAsync and StopAsync. The fake clock
// drives the timer, the fake probe replaces the network, and the database lives in memory.
public class MonitoringWorkerLoopTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    // The limit is only there so that a bug gives a red test instead of a test that hangs.
    private static readonly TimeSpan SafetyLimit = TimeSpan.FromSeconds(10);

    private static readonly ProbeResult ServiceIsUp = new ProbeResult(ServiceStatus.Up, 200, 12, null);

    // Repeats the manual run that found the bug: 16 checks with a 10 second interval and a 5 second
    // poll, and a response time above zero. The passes are run by hand on the exact poll times,
    // because moving the fake clock during a pass also moves the next tick of the fake timer.
    [Fact]
    public async Task ResponseTimeDoesNotPushTheNextCheckOnePollLater()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await RegisterAsync(app, client, "Example");
        using MonitoringWorker worker = app.CreateWorker();
        List<DateTimeOffset> checkTimes = new List<DateTimeOffset>();
        DateTimeOffset start = app.Clock.GetUtcNow();

        // Half a second, because a shorter response time would be hidden by the tolerance in IsDue.
        Task<ProbeResult> ProbeThatTakesHalfASecond(CancellationToken ct)
        {
            checkTimes.Add(app.Clock.GetUtcNow());
            app.Clock.Advance(TimeSpan.FromMilliseconds(500));
            return Task.FromResult(ServiceIsUp);
        }

        app.Probe.Check = ProbeThatTakesHalfASecond;
        await worker.RunPassAsync(CancellationToken.None);

        for (int poll = 1; poll <= 30; poll++)
        {
            app.Clock.SetUtcNow(start.AddSeconds(poll * 5));
            await worker.RunPassAsync(CancellationToken.None);
        }

        AssertEveryGapIs(TimeSpan.FromSeconds(10), checkTimes);
        Assert.Equal(16, checkTimes.Count);
    }

    // Uses the default poll interval on purpose, because that is what a real run uses.
    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(17)]
    public async Task RealIntervalMatchesIntervalSecondsWithTheDefaultPoll(int intervalSeconds)
    {
        MonitoringOptions options = new MonitoringOptions();
        int numberOfTicks = intervalSeconds * 3 / options.PollIntervalSeconds;

        List<DateTimeOffset> checkTimes = await RunLoopAndGetCheckTimesAsync(options, intervalSeconds, numberOfTicks);

        AssertEveryGapIs(TimeSpan.FromSeconds(intervalSeconds), checkTimes);
        Assert.Equal(4, checkTimes.Count);
    }

    [Fact]
    public async Task CheckThatTakesLongerThanTheIntervalIsFollowedByACheckInTheNextPass()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await RegisterAsync(app, client, "Example");
        using MonitoringWorker worker = app.CreateWorker();
        DateTimeOffset start = app.Clock.GetUtcNow();

        Task<ProbeResult> ProbeThatTakesThirtySeconds(CancellationToken ct)
        {
            app.Clock.Advance(TimeSpan.FromSeconds(30));
            return Task.FromResult(ServiceIsUp);
        }

        app.Probe.Check = ProbeThatTakesThirtySeconds;
        await worker.RunPassAsync(CancellationToken.None);
        Assert.Equal(1, app.Probe.Calls);

        app.Clock.SetUtcNow(start.AddSeconds(31));
        await worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(2, app.Probe.Calls);
    }

    // A real timer never ticks at exactly the same millisecond, so this pass comes 2 ms too early.
    [Fact]
    public async Task PassThatComesAFewMillisecondsEarlyStillChecksTheService()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await RegisterAsync(app, client, "Example");
        using MonitoringWorker worker = app.CreateWorker();
        DateTimeOffset start = app.Clock.GetUtcNow();

        app.Clock.SetUtcNow(start.AddMilliseconds(2));
        await worker.RunPassAsync(CancellationToken.None);
        Assert.Equal(1, app.Probe.Calls);

        app.Clock.SetUtcNow(start.AddSeconds(10));
        await worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(2, app.Probe.Calls);
    }

    [Fact]
    public async Task ServiceIsDueExactlyWhenOnlyTheToleranceRemains()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await RegisterAsync(app, client, "Example");
        using MonitoringWorker worker = app.CreateWorker();
        DateTimeOffset start = app.Clock.GetUtcNow();
        await worker.RunPassAsync(CancellationToken.None);

        app.Clock.SetUtcNow(start.AddMilliseconds(9750));
        await worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(2, app.Probe.Calls);
    }

    [Fact]
    public async Task ServiceIsNotDueBeforeOnlyTheToleranceRemains()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await RegisterAsync(app, client, "Example");
        using MonitoringWorker worker = app.CreateWorker();
        DateTimeOffset start = app.Clock.GetUtcNow();
        await worker.RunPassAsync(CancellationToken.None);

        app.Clock.SetUtcNow(start.AddMilliseconds(9740));
        await worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(1, app.Probe.Calls);
    }

    [Fact]
    public async Task FailingServiceDoesNotStopTheOthersAndIsRetriedOnTheNextPass()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService first = await RegisterAsync(app, client, "A first");
        MonitoredService second = await RegisterAsync(app, client, "B second");
        TestLogger logger = new TestLogger();
        using MonitoringWorker worker = CreateWorkerThatPollsEverySecond(app, logger);

        Task<ProbeResult> ProbeThatFailsOnFirstCall(CancellationToken ct)
        {
            if (app.Probe.Calls == 1)
            {
                throw new InvalidOperationException("The service failed.");
            }

            return Task.FromResult(ServiceIsUp);
        }

        app.Probe.Check = ProbeThatFailsOnFirstCall;

        await worker.StartAsync(CancellationToken.None);
        await logger.WaitForPassToFinishAsync();

        Assert.Equal(2, app.Probe.Calls);
        Assert.Empty(await GetHistoryAsync(client, first));
        Assert.Single(await GetHistoryAsync(client, second));
        string error = Assert.Single(logger.GetErrors());
        Assert.Contains(first.Id.ToString(), error);

        app.Clock.Advance(OneSecond);
        await logger.WaitForPassToFinishAsync();

        // Only the failed service is due again, because its failed check saved nothing.
        Assert.Equal(3, app.Probe.Calls);
        Assert.Single(await GetHistoryAsync(client, first));

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FailedPassIsLoggedAndTheNextPassStillRuns()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await RegisterAsync(app, client, "Example");
        TestLogger logger = new TestLogger();
        using MonitoringWorker worker = CreateWorkerThatPollsEverySecond(app, logger);

        app.RepositoryControl.FailGetAll = true;
        await worker.StartAsync(CancellationToken.None);
        await logger.WaitForPassToFinishAsync();

        Assert.Equal(0, app.Probe.Calls);
        Assert.Single(logger.GetErrors());

        app.RepositoryControl.FailGetAll = false;
        app.Clock.Advance(OneSecond);
        await logger.WaitForPassToFinishAsync();

        Assert.Equal(1, app.Probe.Calls);
        Assert.Single(logger.GetErrors());

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancellationThatIsNotShutdownIsLoggedAndTheLoopContinues()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await RegisterAsync(app, client, "Example");
        TestLogger logger = new TestLogger();
        using MonitoringWorker worker = CreateWorkerThatPollsEverySecond(app, logger);

        Task<ProbeResult> ProbeThatIsCancelledOnFirstCall(CancellationToken ct)
        {
            if (app.Probe.Calls == 1)
            {
                throw new OperationCanceledException();
            }

            return Task.FromResult(ServiceIsUp);
        }

        app.Probe.Check = ProbeThatIsCancelledOnFirstCall;

        await worker.StartAsync(CancellationToken.None);
        await logger.WaitForPassToFinishAsync();

        string error = Assert.Single(logger.GetErrors());
        Assert.Contains(service.Id.ToString(), error);

        app.Clock.Advance(OneSecond);
        await logger.WaitForPassToFinishAsync();

        Assert.Equal(2, app.Probe.Calls);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ServiceDeletedDuringThePassIsSkippedWithoutError()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await RegisterAsync(app, client, "A first");
        MonitoredService second = await RegisterAsync(app, client, "B second");
        TestLogger logger = new TestLogger();
        using MonitoringWorker worker = CreateWorkerThatPollsEverySecond(app, logger);

        async Task<ProbeResult> ProbeThatDeletesTheSecondService(CancellationToken ct)
        {
            HttpResponseMessage deleted = await client.DeleteAsync($"/services/{second.Id}", ct);
            deleted.EnsureSuccessStatusCode();
            return ServiceIsUp;
        }

        app.Probe.Check = ProbeThatDeletesTheSecondService;

        await worker.StartAsync(CancellationToken.None);
        await logger.WaitForPassToFinishAsync();

        Assert.Equal(1, app.Probe.Calls);
        Assert.Empty(logger.GetErrors());

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StoppingWhileWaitingForTheTimerEndsTheLoopWithoutError()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        TestLogger logger = new TestLogger();
        using MonitoringWorker worker = CreateWorkerThatPollsEverySecond(app, logger);

        await worker.StartAsync(CancellationToken.None);
        await logger.WaitForPassToFinishAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(TaskStatus.RanToCompletion, worker.ExecuteTask!.Status);
        Assert.Empty(logger.GetErrors());
    }

    [Fact]
    public async Task StoppingDuringACheckSavesNothingAndLeavesNoLock()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await RegisterAsync(app, client, "Example");
        TestLogger logger = new TestLogger();
        using MonitoringWorker worker = CreateWorkerThatPollsEverySecond(app, logger);
        SemaphoreSlim probeStarted = new SemaphoreSlim(0);

        async Task<ProbeResult> ProbeThatHangsUntilCancelled(CancellationToken ct)
        {
            probeStarted.Release();
            await Task.Delay(Timeout.Infinite, ct);
            return ServiceIsUp;
        }

        app.Probe.Check = ProbeThatHangsUntilCancelled;

        await worker.StartAsync(CancellationToken.None);
        await WaitForSignalAsync(probeStarted);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(TaskStatus.RanToCompletion, worker.ExecuteTask!.Status);
        Assert.Empty(logger.GetErrors());
        Assert.Empty(await GetHistoryAsync(client, service));
        ServiceLocks locks = app.Services.GetRequiredService<ServiceLocks>();
        Assert.Equal(0, locks.ActiveCount);
    }

    [Fact]
    public async Task StoppingAfterTheFirstServiceDoesNotCheckTheNext()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await RegisterAsync(app, client, "A first");
        await RegisterAsync(app, client, "B second");
        TestLogger logger = new TestLogger();
        using MonitoringWorker worker = CreateWorkerThatPollsEverySecond(app, logger);
        Task stopping = Task.CompletedTask;

        Task<ProbeResult> ProbeThatStopsTheWorker(CancellationToken ct)
        {
            // Not awaited here, because StopAsync waits for the pass that is running this probe.
            stopping = worker.StopAsync(CancellationToken.None);
            return Task.FromResult(ServiceIsUp);
        }

        app.Probe.Check = ProbeThatStopsTheWorker;

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(SafetyLimit);
        await stopping;

        Assert.Equal(1, app.Probe.Calls);
        Assert.Empty(logger.GetErrors());
    }

    [Fact]
    public async Task DisabledWorkerEndsAtOnceWithoutChecking()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await RegisterAsync(app, client, "Example");
        MonitoringOptions options = new MonitoringOptions();
        options.Enabled = false;
        using MonitoringWorker worker = app.CreateWorker(options, new TestLogger());

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(SafetyLimit);

        Assert.Equal(0, app.Probe.Calls);
    }

    [Fact]
    public async Task FirstPassRunsAtOnceWithoutWaitingForTheTimer()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await RegisterAsync(app, client, "Example");
        TestLogger logger = new TestLogger();
        using MonitoringWorker worker = CreateWorkerThatPollsEverySecond(app, logger);

        // The clock is never moved in this test, so the timer never ticks.
        await worker.StartAsync(CancellationToken.None);
        await logger.WaitForPassToFinishAsync();

        Assert.Equal(1, app.Probe.Calls);

        await worker.StopAsync(CancellationToken.None);
    }

    // A poll of one hour, so a timer on the real clock could never tick before the safety limit.
    [Fact]
    public async Task TimerFollowsTheInjectedClockAndNotTheRealTime()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await RegisterAsync(app, client, "Example");
        TestLogger logger = new TestLogger();
        MonitoringOptions options = new MonitoringOptions();
        options.PollIntervalSeconds = 3600;
        using MonitoringWorker worker = app.CreateWorker(options, logger);

        await worker.StartAsync(CancellationToken.None);
        await logger.WaitForPassToFinishAsync();

        app.Clock.Advance(TimeSpan.FromSeconds(3600));
        await logger.WaitForPassToFinishAsync();

        Assert.Equal(2, app.Probe.Calls);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TicksDuringASlowCheckNeverStartASecondCheck()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await RegisterAsync(app, client, "Example");
        TestLogger logger = new TestLogger();
        using MonitoringWorker worker = CreateWorkerThatPollsEverySecond(app, logger);
        SemaphoreSlim probeStarted = new SemaphoreSlim(0);
        SemaphoreSlim letProbeFinish = new SemaphoreSlim(0);
        int runningChecks = 0;
        int mostChecksAtOnce = 0;

        async Task<ProbeResult> ProbeThatCountsRunningChecks(CancellationToken ct)
        {
            int running = Interlocked.Increment(ref runningChecks);
            if (running > mostChecksAtOnce)
            {
                mostChecksAtOnce = running;
            }

            probeStarted.Release();
            await letProbeFinish.WaitAsync(ct);
            Interlocked.Decrement(ref runningChecks);
            return ServiceIsUp;
        }

        app.Probe.Check = ProbeThatCountsRunningChecks;

        await worker.StartAsync(CancellationToken.None);
        await WaitForSignalAsync(probeStarted);

        app.Clock.Advance(OneSecond);
        app.Clock.Advance(OneSecond);
        app.Clock.Advance(OneSecond);
        letProbeFinish.Release();

        // The first pass, and then one more for the ticks that came while it was running.
        await logger.WaitForPassToFinishAsync();
        await logger.WaitForPassToFinishAsync();

        Assert.Equal(1, mostChecksAtOnce);
        Assert.Equal(1, app.Probe.Calls);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerSkipsServiceWithoutErrorWhileAManualCheckHoldsTheLock()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await RegisterAsync(app, client, "Example");
        TestLogger logger = new TestLogger();
        using MonitoringWorker worker = CreateWorkerThatPollsEverySecond(app, logger);
        SemaphoreSlim probeStarted = new SemaphoreSlim(0);
        SemaphoreSlim letProbeFinish = new SemaphoreSlim(0);

        async Task<ProbeResult> ProbeThatWaitsForTheTest(CancellationToken ct)
        {
            probeStarted.Release();
            await letProbeFinish.WaitAsync(ct);
            return ServiceIsUp;
        }

        app.Probe.Check = ProbeThatWaitsForTheTest;

        Task<HttpResponseMessage> manualCheck = client.PostAsync($"/services/{service.Id}/check", null);
        await WaitForSignalAsync(probeStarted);

        await worker.StartAsync(CancellationToken.None);
        await logger.WaitForPassToFinishAsync();

        Assert.Equal(1, app.Probe.Calls);
        Assert.Empty(logger.GetErrors());

        letProbeFinish.Release();
        HttpResponseMessage response = await manualCheck;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ManualCheckGets409WhileTheWorkerIsCheckingTheService()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await RegisterAsync(app, client, "Example");
        TestLogger logger = new TestLogger();
        using MonitoringWorker worker = CreateWorkerThatPollsEverySecond(app, logger);
        SemaphoreSlim probeStarted = new SemaphoreSlim(0);
        SemaphoreSlim letProbeFinish = new SemaphoreSlim(0);

        async Task<ProbeResult> ProbeThatWaitsForTheTest(CancellationToken ct)
        {
            probeStarted.Release();
            await letProbeFinish.WaitAsync(ct);
            return ServiceIsUp;
        }

        app.Probe.Check = ProbeThatWaitsForTheTest;

        await worker.StartAsync(CancellationToken.None);
        await WaitForSignalAsync(probeStarted);

        HttpResponseMessage response = await client.PostAsync($"/services/{service.Id}/check", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, app.Probe.Calls);

        letProbeFinish.Release();
        await logger.WaitForPassToFinishAsync();
        Assert.Empty(logger.GetErrors());

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ManualCheckMovesTheNextWorkerCheckForward()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await RegisterAsync(app, client, "Example");
        using MonitoringWorker worker = app.CreateWorker();
        DateTimeOffset start = app.Clock.GetUtcNow();

        await worker.RunPassAsync(CancellationToken.None);
        Assert.Equal(1, app.Probe.Calls);

        app.Clock.SetUtcNow(start.AddSeconds(8));
        HttpResponseMessage manualCheck = await client.PostAsync($"/services/{service.Id}/check", null);
        manualCheck.EnsureSuccessStatusCode();
        Assert.Equal(2, app.Probe.Calls);

        // The worker would have checked here if the manual check had not moved the time.
        app.Clock.SetUtcNow(start.AddSeconds(10));
        await worker.RunPassAsync(CancellationToken.None);
        Assert.Equal(2, app.Probe.Calls);

        app.Clock.SetUtcNow(start.AddSeconds(18));
        await worker.RunPassAsync(CancellationToken.None);
        Assert.Equal(3, app.Probe.Calls);
    }

    // The worker is stopped in the middle of its read. If it took the lock before that read, the
    // manual check would get 409 although the worker has nothing to do for this service.
    [Fact]
    public async Task ManualCheckGetsNo409DuringAPassWhereTheServiceIsNotDue()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await RegisterAsync(app, client, "Example");
        using MonitoringWorker worker = app.CreateWorker();

        // The first pass checks the service, so it is not due in the next pass.
        await worker.RunPassAsync(CancellationToken.None);
        Assert.Equal(1, app.Probe.Calls);

        app.RepositoryControl.HoldNextGetById();
        Task pass = worker.RunPassAsync(CancellationToken.None);
        await app.RepositoryControl.WaitUntilHoldIsReachedAsync();

        HttpResponseMessage response = await client.PostAsync($"/services/{service.Id}/check", null);

        app.RepositoryControl.ReleaseHold();
        await pass;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, app.Probe.Calls);
    }

    // The test above only stops the first read, which is outside the lock. This test also notices
    // if the worker goes on to take the lock and read again for a service that is not due.
    [Fact]
    public async Task WorkerNeverHoldsTheLockInAPassWhereTheServiceIsNotDue()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await RegisterAsync(app, client, "Example");
        using MonitoringWorker worker = app.CreateWorker();

        // The first pass checks the service, and is allowed to hold the lock while it does.
        await worker.RunPassAsync(CancellationToken.None);
        app.RepositoryControl.ForgetLockCounts();

        await worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(1, app.Probe.Calls);
        Assert.Equal(0, app.RepositoryControl.GetHighestLockCountDuringRead());
    }

    // The worker reads the service as due and is stopped there. A manual check then runs from start
    // to end. When the worker continues, it must read again and see that the service is no longer due.
    [Fact]
    public async Task WorkerDoesNotCheckAgainWhenAManualCheckFinishedAfterItsFirstRead()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await RegisterAsync(app, client, "Example");
        using MonitoringWorker worker = app.CreateWorker();

        app.RepositoryControl.HoldNextGetById();
        Task pass = worker.RunPassAsync(CancellationToken.None);
        await app.RepositoryControl.WaitUntilHoldIsReachedAsync();

        HttpResponseMessage response = await client.PostAsync($"/services/{service.Id}/check", null);

        app.RepositoryControl.ReleaseHold();
        await pass;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, app.Probe.Calls);
    }

    // Runs the real loop for a number of ticks and returns the time every check started.
    private static async Task<List<DateTimeOffset>> RunLoopAndGetCheckTimesAsync(
        MonitoringOptions options,
        int intervalSeconds,
        int numberOfTicks)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        ServiceConfiguration configuration = new ServiceConfiguration("Example", "https://example.com", intervalSeconds);
        await app.RegisterServiceAsync(client, configuration);
        TestLogger logger = new TestLogger();
        using MonitoringWorker worker = app.CreateWorker(options, logger);
        List<DateTimeOffset> checkTimes = new List<DateTimeOffset>();
        DateTimeOffset start = app.Clock.GetUtcNow();

        Task<ProbeResult> ProbeThatSavesTheTime(CancellationToken ct)
        {
            checkTimes.Add(app.Clock.GetUtcNow());
            return Task.FromResult(ServiceIsUp);
        }

        app.Probe.Check = ProbeThatSavesTheTime;

        await worker.StartAsync(CancellationToken.None);
        await logger.WaitForPassToFinishAsync();

        for (int tick = 1; tick <= numberOfTicks; tick++)
        {
            app.Clock.SetUtcNow(start.AddSeconds(tick * options.PollIntervalSeconds));
            await logger.WaitForPassToFinishAsync();
        }

        await worker.StopAsync(CancellationToken.None);
        return checkTimes;
    }

    private static void AssertEveryGapIs(TimeSpan expectedGap, List<DateTimeOffset> checkTimes)
    {
        for (int i = 1; i < checkTimes.Count; i++)
        {
            TimeSpan gap = checkTimes[i] - checkTimes[i - 1];
            Assert.Equal(expectedGap, gap);
        }
    }

    private static MonitoringWorker CreateWorkerThatPollsEverySecond(TestApp app, TestLogger logger)
    {
        MonitoringOptions options = new MonitoringOptions();
        options.PollIntervalSeconds = 1;
        return app.CreateWorker(options, logger);
    }

    private static async Task<MonitoredService> RegisterAsync(TestApp app, HttpClient client, string name)
    {
        ServiceConfiguration configuration = new ServiceConfiguration(name, "https://example.com", IntervalSeconds: 10);
        return await app.RegisterServiceAsync(client, configuration);
    }

    private static async Task<List<CheckRecord>> GetHistoryAsync(HttpClient client, MonitoredService service)
    {
        List<CheckRecord>? history = await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{service.Id}/history", TestJson.Options);
        Assert.NotNull(history);
        return history;
    }

    private static async Task WaitForSignalAsync(SemaphoreSlim signal)
    {
        bool signalled = await signal.WaitAsync(SafetyLimit);
        Assert.True(signalled, "The expected step did not happen in time.");
    }
}
