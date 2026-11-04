namespace DeadSwitch.Application.Services;

// Bounded process-local locks shared by API mutations and the scheduler.
public sealed class ServiceLocks
{
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public SemaphoreSlim For(Guid id) => _locks[(uint)id.GetHashCode() % (uint)_locks.Length];
}
