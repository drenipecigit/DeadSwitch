namespace DeadSwitch.Domain;

public class CheckRecord
{
    public Guid Id { get; set; }
    public Guid MonitoredServiceId { get; set; }

    // Saved on every record, because a service's URL can change later.
    // The history must still show which address this check actually called.
    public string Url { get; set; } = string.Empty;

    public DateTime CheckedAtUtc { get; set; }
    public ServiceStatus Status { get; set; }

    // Nullable, because a timeout or a connection failure never produces a status code.
    public int? StatusCode { get; set; }

    public long ResponseTimeMs { get; set; }
    public string? FailureReason { get; set; }

    // A letter from SecurityHeaderRule, or null when the response was not assessed.
    public string? SecurityGrade { get; set; }

    // Header names from SecurityHeaderRule. An empty text means that nothing is missing, and null
    // means that the response was not assessed. The difference matters, so they are kept apart.
    public string? MissingSecurityHeaders { get; set; }

    // A fixed text saying why the response got no grade.
    public string? SecurityNotAssessedReason { get; set; }
}
