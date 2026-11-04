namespace DeadSwitch.Infrastructure.Monitoring;

// Its own type, so the probe can tell "we refused to connect" (UNKNOWN) from "the service did not
// answer" (DOWN). It inherits HttpRequestException, because that is what HttpClient callers handle.
internal sealed class DestinationBlockedException : HttpRequestException
{
    public DestinationBlockedException()
        : base("Destination rejected.")
    {
    }
}
