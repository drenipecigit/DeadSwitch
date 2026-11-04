namespace DeadSwitch.Application.Services;

// Stops the API and the worker from working on the same service at the same time.
// Registered as a singleton, so everyone shares the same locks. They only work within one process.
//
// There is a fixed number of locks, so memory does not grow with every service ever seen.
// Known limitation: two different services can end up sharing a lock and then block each other.
public sealed class ServiceLocks
{
    private const int LockCount = 64;

    private readonly SemaphoreSlim[] _locks = new SemaphoreSlim[LockCount];

    public ServiceLocks()
    {
        for (int i = 0; i < LockCount; i++)
        {
            // SemaphoreSlim instead of the lock keyword, because lock cannot be held across an await.
            // (1, 1) means that only one caller at a time can hold it.
            _locks[i] = new SemaphoreSlim(1, 1);
        }
    }

    public SemaphoreSlim For(Guid id)
    {
        // The cast to uint removes the sign, because a negative hash would give a negative index.
        uint hash = (uint)id.GetHashCode();
        uint index = hash % LockCount;
        return _locks[index];
    }
}
