using DeadSwitch.Domain;

namespace DeadSwitch.Application.Services;

// One row on the dashboard: a service together with the result of its latest check.
// The values from ResponseTimeMs and on are null when the current URL has not been checked yet.
public record ServiceOverview(
    Guid Id,
    string Name,
    string Url,
    ServiceStatus CurrentStatus,
    ServiceStatus? LastKnownStatus,
    DateTimeOffset? LastCheckedAt,
    bool IsEnabled,
    long? ResponseTimeMs,
    string? FailureReason,
    string? SecurityGrade,
    string? MissingSecurityHeaders,
    string? SecurityNotAssessedReason,
    DateTime? CertificateNotAfterUtc,
    int? CertificateDaysLeft,
    string? CertificateExpiryText,
    string? CertificateNotAssessedReason,
    RedirectFinding? RedirectFinding,
    string? RedirectTargetHost);
