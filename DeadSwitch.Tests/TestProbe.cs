using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

// Replaces the real HTTP probe, so no test depends on an external website.
internal sealed class TestProbe : IHealthProbe
{
    // A field and not a property, because Interlocked needs a reference to the variable.
    public int Calls;

    public ProbeResult Result = new ProbeResult(ServiceStatus.Up, 200, 12, null);

    // When set, a test decides how the check behaves, for example hanging until it is cancelled.
    public Func<CancellationToken, Task<ProbeResult>>? Check;

    public Task<ProbeResult> CheckAsync(string url, int timeoutSeconds, CancellationToken cancellationToken)
    {
        // Interlocked keeps the count correct if two requests call the probe at the same time.
        Interlocked.Increment(ref Calls);

        if (Check != null)
        {
            return Check(cancellationToken);
        }

        return Task.FromResult(Result);
    }
}
