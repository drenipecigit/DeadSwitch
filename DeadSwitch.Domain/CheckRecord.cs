namespace DeadSwitch.Domain;

public class CheckRecord
{
    public Guid Id { get; set; }
    public Guid MonitoredServiceId { get; set; }

    // Saved on every record, because a service's URL can change later.
    // The history must still show which address this check actually called.
    public string Url { get; set; } = string.Empty;

    public DateTime CheckedAtUtc { get; set; }
    public string Status { get; set; } = "DOWN";

    // Nullable, because a timeout or a connection failure never produces a status code.
    public int? StatusCode { get; set; }

    public long ResponseTimeMs { get; set; }
    public string? FailureReason { get; set; }
}
