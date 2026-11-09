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

    // When the certificate seen in this check expires. Null when this check saw no valid certificate.
    public DateTime? CertificateNotAfterUtc { get; set; }

    // A fixed text saying why this check has no certificate date.
    public string? CertificateNotAssessedReason { get; set; }

    // Null when this check learned nothing about a redirect, and on every row older than this column.
    public RedirectFinding? RedirectFinding { get; set; }

    // A normalized host name or address, and only for OtherHost. The rest of the redirect is never kept.
    public string? RedirectTargetHost { get; set; }
}
