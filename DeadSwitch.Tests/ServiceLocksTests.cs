using DeadSwitch.Application.Services;

namespace DeadSwitch.Tests;

public class ServiceLocksTests
{
    // 65 because the old design had 64 shared locks, which guaranteed that two of these ids collided.
    [Fact]
    public void DifferentServicesNeverShareALock()
    {
        ServiceLocks locks = new ServiceLocks();

        for (int i = 0; i < 65; i++)
        {
            Assert.True(locks.TryEnter(Guid.NewGuid()));
        }

        Assert.Equal(65, locks.ActiveCount);
    }

    [Fact]
    public void SameServiceIsBlockedUntilReleased()
    {
        ServiceLocks locks = new ServiceLocks();
        Guid id = Guid.NewGuid();

        Assert.True(locks.TryEnter(id));
        Assert.False(locks.TryEnter(id));

        locks.Release(id);

        Assert.True(locks.TryEnter(id));
    }

    [Fact]
    public async Task WaitingCallerGetsLockAfterRelease()
    {
        ServiceLocks locks = new ServiceLocks();
        Guid id = Guid.NewGuid();
        Assert.True(locks.TryEnter(id));

        Task waiter = locks.EnterAsync(id, CancellationToken.None);
        Assert.False(waiter.IsCompleted);

        locks.Release(id);

        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TryEnterFailsWhileAnotherCallerWaits()
    {
        ServiceLocks locks = new ServiceLocks();
        Guid id = Guid.NewGuid();
        Assert.True(locks.TryEnter(id));
        Task waiter = locks.EnterAsync(id, CancellationToken.None);

        locks.Release(id);

        // The lock now belongs to the waiter, so a new check must not get in before it.
        Assert.False(locks.TryEnter(id));

        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        locks.Release(id);

        Assert.True(locks.TryEnter(id));
    }

    [Fact]
    public async Task CancelledWaiterLeavesLockUsable()
    {
        ServiceLocks locks = new ServiceLocks();
        Guid id = Guid.NewGuid();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        Assert.True(locks.TryEnter(id));
        Task waiter = locks.EnterAsync(id, cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        locks.Release(id);

        Assert.Equal(0, locks.ActiveCount);
        Assert.True(locks.TryEnter(id));
    }

    [Fact]
    public async Task LocksAreRemovedAfterUse()
    {
        ServiceLocks locks = new ServiceLocks();

        for (int i = 0; i < 1000; i++)
        {
            Guid id = Guid.NewGuid();
            await locks.EnterAsync(id, CancellationToken.None);
            locks.Release(id);
        }

        Assert.Equal(0, locks.ActiveCount);
    }

    [Fact]
    public async Task ManyCallersOnSameServiceNeverOverlap()
    {
        ServiceLocks locks = new ServiceLocks();
        Guid id = Guid.NewGuid();
        int insideCount = 0;
        bool overlapped = false;

        List<Task> callers = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            callers.Add(Task.Run(async () =>
            {
                await locks.EnterAsync(id, CancellationToken.None);
                try
                {
                    if (Interlocked.Increment(ref insideCount) > 1)
                    {
                        overlapped = true;
                    }

                    // Lets other callers run while this one is inside, so an overlap can happen.
                    await Task.Delay(1);
                    Interlocked.Decrement(ref insideCount);
                }
                finally
                {
                    locks.Release(id);
                }
            }));
        }

        await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(overlapped);
        Assert.Equal(0, locks.ActiveCount);
    }
}
