using DeadSwitch.Application.Interfaces;

namespace DeadSwitch.Tests;

// Replaces the real notifier, so no ordinary test can ever send anything to Discord.
internal sealed class TestNotifier : INotifier
{
    private readonly object _sync = new object();
    private readonly List<StatusChange> _changes = new List<StatusChange>();

    // When set, the notifier throws this one instead of remembering the change.
    public Exception? ExceptionToThrow;

    // When set, a test can look at the application at the exact moment a notification is sent.
    public Func<Task>? RunWhenNotified;

    public List<StatusChange> GetChanges()
    {
        lock (_sync)
        {
            // A copy, because the worker can notify on another thread while the test reads the list.
            return new List<StatusChange>(_changes);
        }
    }

    public async Task NotifyStatusChangedAsync(StatusChange change)
    {
        if (RunWhenNotified != null)
        {
            await RunWhenNotified();
        }

        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }

        lock (_sync)
        {
            _changes.Add(change);
        }
    }
}
