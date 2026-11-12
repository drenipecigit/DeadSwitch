using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;

namespace DeadSwitch.Application.Services;

// Compares a new check with the earlier checks in the history. Nothing is kept in memory, so a
// restarted worker gives the same answer, and no warning is repeated or missed because of a restart.
public sealed class SecurityWarningFinder
{
    private readonly IMonitoredServiceRepository _repository;
    private readonly SecurityWarningSettings _settings;

    public SecurityWarningFinder(IMonitoredServiceRepository repository, SecurityWarningSettings settings)
    {
        _repository = repository;
        _settings = settings;
    }

    // Must run before the service and the record are saved, or the record would be its own baseline.
    // Headers and certificate are looked at separately: a redirect has no assessed headers but can
    // have a valid certificate, and a certificate that was not seen says nothing about the headers.
    public async Task<List<SecurityWarning>> FindAsync(MonitoredService service, CheckRecord record, CancellationToken ct)
    {
        List<SecurityWarning> warnings = new List<SecurityWarning>();

        SecurityWarning? headerWarning = await FindHeaderWarningAsync(service, record, ct);
        if (headerWarning != null)
        {
            warnings.Add(headerWarning);
        }

        SecurityWarning? certificateWarning = await FindCertificateWarningAsync(service, record, ct);
        if (certificateWarning != null)
        {
            warnings.Add(certificateWarning);
        }

        SecurityWarning? redirectWarning = await FindRedirectWarningAsync(service, record, ct);
        if (redirectWarning != null)
        {
            warnings.Add(redirectWarning);
        }

        return warnings;
    }

    private async Task<SecurityWarning?> FindRedirectWarningAsync(MonitoredService service, CheckRecord record, CancellationToken ct)
    {
        if (!RedirectWarningRule.IsExternal(record.RedirectFinding))
        {
            return null;
        }

        // A new service or a new URL has nothing earlier to be compared with.
        if (!service.LastCheckedAt.HasValue)
        {
            return null;
        }

        // Only timeouts and errors so far, or rows older than this feature: still nothing to compare with.
        bool hasEarlierFinding = await _repository.HasRedirectFindingAsync(service.Id, service.Url, ct);
        if (!hasEarlierFinding)
        {
            return null;
        }

        // The ! is safe: IsExternal has just said that there is a finding.
        RedirectFinding finding = record.RedirectFinding!.Value;
        DateTime rememberedSince = record.CheckedAtUtc.AddDays(-RedirectWarningRule.RememberDays);

        bool alreadySeen = await _repository.HasSeenRedirectTargetAsync(service.Id, service.Url, finding, record.RedirectTargetHost, rememberedSince, ct);
        if (alreadySeen)
        {
            return null;
        }

        return new SecurityWarning(service.Id, service.Name, ToWarningKind(finding), Array.Empty<string>(), null, CheckTimeOf(record), record.RedirectTargetHost);
    }

    private static SecurityWarningKind ToWarningKind(RedirectFinding finding)
    {
        if (finding == RedirectFinding.OtherHost)
        {
            return SecurityWarningKind.RedirectToOtherHost;
        }

        if (finding == RedirectFinding.InternalTarget)
        {
            return SecurityWarningKind.RedirectToInternalTarget;
        }

        return SecurityWarningKind.RedirectUnreadable;
    }

    private async Task<SecurityWarning?> FindHeaderWarningAsync(MonitoredService service, CheckRecord record, CancellationToken ct)
    {
        // Null means that this check did not assess the headers: a timeout, a redirect or an error.
        // Such a check must neither warn nor change what the next check is compared with.
        if (record.MissingSecurityHeaders == null)
        {
            return null;
        }

        // A new service or a new URL has nothing earlier to be compared with.
        if (!service.LastCheckedAt.HasValue)
        {
            return null;
        }

        CheckRecord? baseline = await _repository.GetHeaderBaselineAsync(service.Id, service.Url, ct);
        if (baseline == null || baseline.MissingSecurityHeaders == null)
        {
            return null;
        }

        List<string> disappeared = SecurityHeaderRule.FindDisappeared(baseline.MissingSecurityHeaders, record.MissingSecurityHeaders);
        if (disappeared.Count == 0)
        {
            return null;
        }

        return new SecurityWarning(service.Id, service.Name, SecurityWarningKind.HeadersDisappeared, disappeared, null, CheckTimeOf(record));
    }

    private async Task<SecurityWarning?> FindCertificateWarningAsync(MonitoredService service, CheckRecord record, CancellationToken ct)
    {
        CertificateFinding finding = CertificateWarningRule.Classify(record.CertificateNotAfterUtc, record.CertificateNotAssessedReason);

        // Only these two are verdicts. A timeout or a handshake that was not seen says nothing.
        if (finding != CertificateFinding.Valid && finding != CertificateFinding.Rejected)
        {
            return null;
        }

        CheckRecord? baseline = null;

        // A new service or a new URL has nothing earlier to be compared with.
        if (service.LastCheckedAt.HasValue)
        {
            baseline = await _repository.GetCertificateBaselineAsync(service.Id, service.Url, ct);
        }

        CertificateFinding? baselineFinding = null;
        if (baseline != null)
        {
            baselineFinding = CertificateWarningRule.Classify(baseline.CertificateNotAfterUtc, baseline.CertificateNotAssessedReason);
        }

        if (finding == CertificateFinding.Rejected)
        {
            if (CertificateWarningRule.IsNewlyRejected(baselineFinding, finding))
            {
                return new SecurityWarning(service.Id, service.Name, SecurityWarningKind.CertificateRejected, Array.Empty<string>(), null, CheckTimeOf(record));
            }

            return null;
        }

        return FindExpiryWarning(service, record, baseline, baselineFinding);
    }

    private SecurityWarning? FindExpiryWarning(MonitoredService service, CheckRecord record, CheckRecord? baseline, CertificateFinding? baselineFinding)
    {
        int? currentThreshold = ThresholdAtTheTimeOf(record);

        // Stays null without a valid baseline, so a first check with few days left also warns. It is
        // a fact about the certificate, and the next warning would otherwise come at the last threshold.
        int? previousThreshold = null;
        if (baseline != null && baselineFinding == CertificateFinding.Valid)
        {
            previousThreshold = ThresholdAtTheTimeOf(baseline);
        }

        if (!CertificateWarningRule.IsWorseThreshold(currentThreshold, previousThreshold))
        {
            return null;
        }

        return new SecurityWarning(service.Id, service.Name, SecurityWarningKind.CertificateExpiringSoon, Array.Empty<string>(), currentThreshold, CheckTimeOf(record));
    }

    // Counted from the time of that check, and not from now, so that an earlier check is judged as it
    // was then. The threshold it reached is known without saving anything extra.
    // The ! is safe: this is only called for a check that Classify has called Valid, which means a date.
    private int? ThresholdAtTheTimeOf(CheckRecord record)
    {
        int daysLeft = CertificateExpiryRule.DaysLeft(record.CertificateNotAfterUtc!.Value, CheckTimeOf(record));
        return CertificateWarningRule.ThresholdFor(daysLeft, _settings.CertificateWarningDays);
    }

    // The time a check started. The record keeps it as a UTC DateTime, and a warning carries it on.
    private static DateTimeOffset CheckTimeOf(CheckRecord record)
    {
        return new DateTimeOffset(record.CheckedAtUtc);
    }
}
