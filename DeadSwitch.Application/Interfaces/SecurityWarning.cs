namespace DeadSwitch.Application.Interfaces;

public enum SecurityWarningKind
{
    HeadersDisappeared,
    CertificateRejected,
    CertificateExpiringSoon,
}

// Security: holds nothing written by the monitored site. The header names come from our own fixed list
// and the number of days is one of our own thresholds, so a raw value has no field to travel in.
public record SecurityWarning(
    Guid ServiceId,
    string ServiceName,
    SecurityWarningKind Kind,
    IReadOnlyList<string> DisappearedHeaders,
    int? ThresholdDays);
