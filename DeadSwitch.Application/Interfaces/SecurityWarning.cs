namespace DeadSwitch.Application.Interfaces;

public enum SecurityWarningKind
{
    HeadersDisappeared,
    CertificateRejected,
    CertificateExpiringSoon,
    RedirectToOtherHost,
    RedirectToInternalTarget,
    RedirectUnreadable,
}

// Security: the header names come from our own fixed list and the number of days is one of our own
// thresholds. RedirectTargetHost is the only value chosen by the monitored site. It has passed the
// allowlist in RedirectTargetParser, so it can only hold letters, digits and the signs of an address.
public record SecurityWarning(
    Guid ServiceId,
    string ServiceName,
    SecurityWarningKind Kind,
    IReadOnlyList<string> DisappearedHeaders,
    int? ThresholdDays,
    string? RedirectTargetHost = null);
