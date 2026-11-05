namespace DeadSwitch.Application.Services;

// Thrown when a service's configuration or URL is not acceptable. The API turns it into a 400.
// The message is shown to the client, so it must be a fixed text and must never contain input.
// It does not inherit from ArgumentException: only errors we raise on purpose may become a 400 with
// their text, and not every ArgumentException from somewhere inside the framework.
public sealed class ServiceValidationException : Exception
{
    public ServiceValidationException(string message) : base(message)
    {
    }
}
