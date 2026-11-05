namespace DeadSwitch.Application.Interfaces;

// Application only knows that a change is announced, not where. A new channel is a new class.
public interface INotifier
{
    // No CancellationToken on purpose: the check is already saved when this runs. If the caller's
    // cancellation stopped the message, the status would not change again and the alarm would be lost.
    // An implementation must therefore limit its own time.
    Task NotifyStatusChangedAsync(StatusChange change);
}
