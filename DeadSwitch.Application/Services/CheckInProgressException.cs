namespace DeadSwitch.Application.Services;

// Thrown when a check cannot start because the service's lock is taken.
// The API turns it into a 409, and the worker skips the service until the next pass.
public sealed class CheckInProgressException : Exception
{
    public CheckInProgressException() : base("A check or configuration change is already in progress. Retry later.")
    {
    }
}
