namespace DeadSwitch.Application.Services;

// Thrown when a notification could not be delivered. The text is written to the log, so it must only
// ever be a fixed text from our own code. Never give it an inner exception: that text can hold the
// secret address of the channel.
public sealed class NotificationFailedException : Exception
{
    public NotificationFailedException(string message) : base(message)
    {
    }
}
