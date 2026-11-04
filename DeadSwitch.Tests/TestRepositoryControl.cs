namespace DeadSwitch.Tests;

// The switches a test uses to make TestRepository fail or stop.
internal sealed class TestRepositoryControl
{
    private readonly object _sync = new object();
    private bool _holdNextGetById;

    // Håller kvar workern mitt i ett pass, så att testet kan göra ett anrop exakt då.
    private readonly SemaphoreSlim _holdReached = new SemaphoreSlim(0);
    private readonly SemaphoreSlim _holdReleased = new SemaphoreSlim(0);

    // When true, GetAllAsync throws. Volatile, because the worker reads it on another thread.
    public volatile bool FailGetAll;

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
