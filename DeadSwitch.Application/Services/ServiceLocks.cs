namespace DeadSwitch.Application.Services;

// Stops the API and the worker from working on the same service at the same time.
// Registered as a singleton, so everyone shares the same locks. They only work within one process.
//
// Every service has its own lock, and the lock only exists while someone holds or waits for it.
// Keeping locks forever would not be safe: a lock is taken before the code knows the service exists,
// so requests with random ids could otherwise make the memory grow without limit.
public sealed class ServiceLocks
{
    // Protects the dictionary and the counters. The keyword lock is fine here, because the code
    // inside it is short and never awaits anything.
    private readonly object _sync = new object();

    private readonly Dictionary<Guid, LockEntry> _entries = new Dictionary<Guid, LockEntry>();

    // Lets the tests prove that no locks are left behind.
    public int ActiveCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    // Waits until the lock is free. The caller must call Release when done.
    public async Task EnterAsync(Guid id, CancellationToken ct)
    {
        LockEntry? entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out entry))
            {
                entry = new LockEntry(freeSlots: 1);
                _entries.Add(id, entry);
            }

            entry.UserCount++;
        }

        try
        {
            await entry.Gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // This caller gave up without getting the lock, so it must not stay counted as a user.
            // Otherwise the entry would never be removed.
            RemoveUser(id, entry);
            throw;
        }
    }

    // Takes the lock only if nobody holds or waits for it. The caller must call Release if this
    // returns true.
    public bool TryEnter(Guid id)
    {
        lock (_sync)
        {
            if (_entries.ContainsKey(id))
            {
                return false;
            }

            // No free slots: the lock is already taken by this caller when the entry is created.
            LockEntry entry = new LockEntry(freeSlots: 0);
            entry.UserCount = 1;
            _entries.Add(id, entry);
            return true;
        }
    }

    public void Release(Guid id)
    {
        LockEntry entry;
        lock (_sync)
        {
            entry = _entries[id];
        }

        bool wasLastUser = RemoveUser(id, entry);

        // Only needed when someone is waiting. Without waiters the entry is already gone.
        if (!wasLastUser)
        {
            entry.Gate.Release();
        }
    }

    private bool RemoveUser(Guid id, LockEntry entry)
    {
        lock (_sync)
        {
            entry.UserCount--;
            if (entry.UserCount > 0)
            {
                return false;
            }

            _entries.Remove(id);
            return true;
        }
    }

    private sealed class LockEntry
    {
        // SemaphoreSlim instead of the lock keyword, because lock cannot be held across an await.
        // It is not disposed, because it only needs that if AvailableWaitHandle is used. Disposing
        // it could also fail: Release can be on its way to call Gate.Release while the last waiter
        // cancels and removes the entry.
        public SemaphoreSlim Gate { get; }

        // How many callers hold or wait for this lock. Only changed inside lock (_sync).
        public int UserCount { get; set; }

        public LockEntry(int freeSlots)
        {
            Gate = new SemaphoreSlim(freeSlots, 1);
        }
    }
}
