namespace DeadSwitch.Application.Interfaces;

// An interface, so the Application layer does not need to know about HTTP,
// and so the tests can replace the real network call with a fake.
public interface IHealthProbe
{
    Task<ProbeResult> CheckAsync(string url, int timeoutSeconds, CancellationToken cancellationToken);
}
