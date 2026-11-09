using DeadSwitch.Domain;

namespace DeadSwitch.Application.Interfaces;

// A record, because a result is only data: it is never changed after it has been created.
// SecurityHeaders is null when no response was received, because then there is nothing to look at.
// Certificate is null when nothing can be said about the certificate.
public record ProbeResult(
    ServiceStatus Status,
    int? StatusCode,
    long ResponseTimeMs,
    string? FailureReason,
    SecurityHeaderCheck? SecurityHeaders = null,
    CertificateCheck? Certificate = null);
