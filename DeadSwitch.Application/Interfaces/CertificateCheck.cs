namespace DeadSwitch.Application.Interfaces;

// The result of looking at a check's TLS certificate. Only a date and our own fixed texts: a text from
// a certificate is written by whoever made the certificate, so none of it is kept.
public record CertificateCheck(DateTime? NotAfterUtc, string? NotAssessedReason);
