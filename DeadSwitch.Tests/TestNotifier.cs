using DeadSwitch.Application.Interfaces;

namespace DeadSwitch.Tests;

// Replaces the real notifier, so no ordinary test can ever send anything to Discord.
internal sealed class TestNotifier : INotifier
{
    private readonly object _sync = new object();
    private readonly List<StatusChange> _changes = new List<StatusChange>();
    private readonly List<SecurityWarning> _securityWarnings = new List<SecurityWarning>();
    private readonly List<string> _sendOrder = new List<string>();

    public const string StatusSent = "status";
    public const string WarningSent = "warning";

    // When set, the notifier throws this one instead of remembering the change.
    public Exception? ExceptionToThrow;

    // When above zero, this many status changes throw before any is remembered.
    public int StatusFailuresLeft;

    // When above zero, this many security warnings throw before any is remembered.
    public int SecurityWarningFailuresLeft;

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

    public List<SecurityWarning> GetSecurityWarnings()
    {
        lock (_sync)
        {
            return new List<SecurityWarning>(_securityWarnings);
        }
    }

    public List<string> GetSendOrder()
    {
        lock (_sync)
        {
            return new List<string>(_sendOrder);
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

        if (StatusFailuresLeft > 0)
        {
            StatusFailuresLeft--;
            throw new InvalidOperationException("The test made the status change fail.");
        }

        lock (_sync)
        {
            _changes.Add(change);
            _sendOrder.Add(StatusSent);
        }
    }

    public async Task NotifySecurityWarningAsync(SecurityWarning warning)
    {
        if (RunWhenNotified != null)
        {
            await RunWhenNotified();
        }

        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }

        if (SecurityWarningFailuresLeft > 0)
        {
            SecurityWarningFailuresLeft--;
            throw new InvalidOperationException("The test made the warning fail.");
        }

        lock (_sync)
        {
            _securityWarnings.Add(warning);
            _sendOrder.Add(WarningSent);
        }
    }
}
