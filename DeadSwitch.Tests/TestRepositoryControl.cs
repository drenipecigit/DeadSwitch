namespace DeadSwitch.Tests;

// The switches a test uses to make TestRepository fail or stop.
internal sealed class TestRepositoryControl
{
    private readonly object _sync = new object();
    private bool _holdNextGetById;
    private int _highestLockCountDuringRead;

    // Holds the worker in the middle of a pass, so the test can make a call at exactly that moment.
    private readonly SemaphoreSlim _holdReached = new SemaphoreSlim(0);
    private readonly SemaphoreSlim _holdReleased = new SemaphoreSlim(0);

    // When true, GetAllAsync throws. Volatile, because the worker reads it on another thread.
    public volatile bool FailGetAll;

    // When set, GetAllAsync throws this one, so a test can choose the type and the text.
    public volatile Exception? ExceptionFromGetAll;

    // When true, SaveCheckAsync throws, so a test can see what happens when a check cannot be saved.
    public volatile bool FailSaveCheck;

    // When set, reading a baseline throws this one, so a test can see a database failure at that moment.
    public volatile Exception? ExceptionFromBaseline;

    // When set, runs just before a baseline is read, so a test can start a shutdown at exactly that moment.
    public volatile Action? RunAtBaseline;

    // Called by TestRepository before it reads a baseline.
    public void RunBaselineSwitches()
    {
        if (ExceptionFromBaseline != null)
        {
            throw ExceptionFromBaseline;
        }

        if (RunAtBaseline != null)
        {
            RunAtBaseline();
        }
    }

    public void RememberLockCount(int lockCount)
    {
        lock (_sync)
        {
            if (lockCount > _highestLockCountDuringRead)
            {
                _highestLockCountDuringRead = lockCount;
            }
        }
    }

    public void ForgetLockCounts()
    {
        lock (_sync)
        {
            _highestLockCountDuringRead = 0;
        }
    }

    public int GetHighestLockCountDuringRead()
    {
        lock (_sync)
        {
            return _highestLockCountDuringRead;
        }
    }

    public void HoldNextGetById()
    {
        lock (_sync)
        {
            _holdNextGetById = true;
        }
    }

    public async Task WaitUntilHoldIsReachedAsync()
    {
        // The limit is only there so that a bug gives a red test instead of a test that hangs.
        bool reached = await _holdReached.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(reached, "No call reached the hold in time.");
    }

    public void ReleaseHold()
    {
        _holdReleased.Release();
    }

    // Called by TestRepository. Only the first read after HoldNextGetById stops here.
    public async Task HoldIfRequestedAsync(CancellationToken ct)
    {
        bool shouldHold;

        lock (_sync)
        {
            shouldHold = _holdNextGetById;
            _holdNextGetById = false;
        }

        if (shouldHold)
        {
            _holdReached.Release();
            await _holdReleased.WaitAsync(ct);
        }
    }
}
