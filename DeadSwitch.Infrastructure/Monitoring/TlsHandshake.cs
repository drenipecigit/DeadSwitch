namespace DeadSwitch.Infrastructure.Monitoring;

// What is kept from one TLS handshake. NotAfterUtc is null when the certificate was rejected, because
// nothing in a certificate that failed validation can be trusted.
internal sealed record TlsHandshake(DateTime? NotAfterUtc, bool IsRejected, DateTimeOffset SeenAt);
