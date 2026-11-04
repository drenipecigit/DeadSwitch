namespace DeadSwitch.Application.Interfaces;

// A record, because a result is only data: it is never changed after it has been created.
public record ProbeResult(string Status, int? StatusCode, long ResponseTimeMs, string? FailureReason);
