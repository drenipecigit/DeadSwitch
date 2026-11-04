namespace DeadSwitch.Application.Interfaces;

public record ProbeResult(string Status, int? StatusCode, long ResponseTimeMs, string? FailureReason);

public interface IHealthProbe
{
    Task<ProbeResult> CheckAsync(string url, int timeoutSeconds, CancellationToken cancellationToken);
}
