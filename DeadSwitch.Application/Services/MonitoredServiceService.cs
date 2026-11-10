using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;
using Microsoft.Extensions.Logging;

namespace DeadSwitch.Application.Services;

public class MonitoredServiceService
{
    // A pass never arrives on the exact millisecond. Without this tolerance a service would sometimes
    // miss a pass, so a check may start up to 250 ms early.
    private static readonly TimeSpan DueTolerance = TimeSpan.FromMilliseconds(250);

    private readonly IMonitoredServiceRepository _repository;
    private readonly IHealthProbe _probe;
    private readonly ServiceLocks _locks;

    // TimeProvider instead of DateTime.UtcNow, so the tests can control the time.
    private readonly TimeProvider _clock;

    private readonly INotifier _notifier;
    private readonly SecurityWarningFinder _securityWarningFinder;
    private readonly ILogger<MonitoredServiceService> _logger;

    public MonitoredServiceService(
        IMonitoredServiceRepository repository,
        IHealthProbe probe,
        ServiceLocks locks,
        TimeProvider clock,
        INotifier notifier,
        SecurityWarningFinder securityWarningFinder,
        ILogger<MonitoredServiceService> logger)
    {
        _repository = repository;
        _probe = probe;
        _locks = locks;
        _clock = clock;
        _notifier = notifier;
        _securityWarningFinder = securityWarningFinder;
        _logger = logger;
    }

    public async Task<MonitoredService> RegisterAsync(ServiceConfiguration configuration, CancellationToken ct = default)
    {
        configuration.Validate();

        MonitoredService service = new MonitoredService();
        service.Id = Guid.NewGuid();
        Apply(service, configuration);

        await _repository.AddAsync(service, ct);
        return service;
    }

    public Task<List<MonitoredService>> GetAllAsync(CancellationToken ct = default)
    {
        return _repository.GetAllAsync(ct);
    }

    public async Task<List<ServiceOverview>> GetOverviewAsync(CancellationToken ct = default)
    {
        List<MonitoredService> services = await _repository.GetAllAsync(ct);
        List<ServiceOverview> overview = new List<ServiceOverview>();

        foreach (MonitoredService service in services)
        {
            long? responseTimeMs = null;
            string? failureReason = null;
            string? securityGrade = null;
            string? missingSecurityHeaders = null;
            string? securityNotAssessedReason = null;
            DateTime? certificateNotAfterUtc = null;
            int? certificateDaysLeft = null;
            string? certificateExpiryText = null;
            string? certificateNotAssessedReason = null;
            RedirectFinding? redirectFinding = null;
            string? redirectTargetHost = null;

            // A new URL empties LastCheckedAt but keeps the history. Without this check the row would
            // show the response time of the old address, also after changing back to an earlier URL.
            if (service.LastCheckedAt.HasValue)
            {
                List<CheckRecord> latest = await _repository.GetHistoryAsync(service.Id, 0, 1, ct);
                if (latest.Count > 0)
                {
                    responseTimeMs = latest[0].ResponseTimeMs;
                    failureReason = latest[0].FailureReason;
                    securityGrade = latest[0].SecurityGrade;
                    missingSecurityHeaders = latest[0].MissingSecurityHeaders;
                    securityNotAssessedReason = latest[0].SecurityNotAssessedReason;

                    // Only the newest check counts: an older date must never be shown after a check
                    // that saw no valid certificate. The days are counted from now and not from the
                    // check, so a certificate can expire while no checks are running.
                    certificateNotAfterUtc = latest[0].CertificateNotAfterUtc;
                    certificateNotAssessedReason = latest[0].CertificateNotAssessedReason;
                    redirectFinding = latest[0].RedirectFinding;
                    redirectTargetHost = latest[0].RedirectTargetHost;

                    if (certificateNotAfterUtc.HasValue)
                    {
                        int daysLeft = CertificateExpiryRule.DaysLeft(certificateNotAfterUtc.Value, _clock.GetUtcNow());
                        certificateDaysLeft = daysLeft;
                        certificateExpiryText = CertificateExpiryRule.TextFor(daysLeft);
                    }
                }
            }

            // Named arguments, because several of them are text that may be null. If two swapped places
            // without the names, the compiler would not notice.
            overview.Add(new ServiceOverview(
                Id: service.Id,
                Name: service.Name,
                Url: service.Url,
                CurrentStatus: service.CurrentStatus,
                LastKnownStatus: service.LastKnownStatus,
                LastCheckedAt: service.LastCheckedAt,
                IsEnabled: service.IsEnabled,
                ResponseTimeMs: responseTimeMs,
                FailureReason: failureReason,
                SecurityGrade: securityGrade,
                MissingSecurityHeaders: missingSecurityHeaders,
                SecurityNotAssessedReason: securityNotAssessedReason,
                CertificateNotAfterUtc: certificateNotAfterUtc,
                CertificateDaysLeft: certificateDaysLeft,
                CertificateExpiryText: certificateExpiryText,
                CertificateNotAssessedReason: certificateNotAssessedReason,
                RedirectFinding: redirectFinding,
                RedirectTargetHost: redirectTargetHost));
        }

        return overview;
    }

    public Task<MonitoredService?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return _repository.GetByIdAsync(id, ct);
    }

    public async Task<MonitoredService?> UpdateAsync(Guid id, ServiceConfiguration configuration, CancellationToken ct = default)
    {
        configuration.Validate();

        // Wait for the lock, so an update never overwrites the result of a running check.
        await _locks.EnterAsync(id, ct);
        try
        {
            MonitoredService? service = await _repository.GetByIdAsync(id, ct);
            if (service is null)
            {
                return null;
            }

            // The old status described the old address, so it says nothing about the new one.
            if (service.Url != configuration.Url.Trim())
            {
                service.CurrentStatus = ServiceStatus.Unknown;
                service.LastKnownStatus = null;
                service.LastCheckedAt = null;
                service.FailedChecksInARow = 0;
            }

            Apply(service, configuration);
            await _repository.UpdateAsync(service, ct);
            return service;
        }
        finally
        {
            _locks.Release(id);
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _locks.EnterAsync(id, ct);
        try
        {
            MonitoredService? service = await _repository.GetByIdAsync(id, ct);
            if (service is null)
            {
                return false;
            }

            await _repository.DeleteAsync(service, ct);
            return true;
        }
        finally
        {
            _locks.Release(id);
        }
    }

    // Returns null both when the service does not exist and when onlyIfDue skips it.
    // Reads from top to bottom: is it due, take the lock, check and save, release the lock, notify.
    public async Task<CheckRecord?> CheckAsync(Guid id, bool onlyIfDue = false, CancellationToken ct = default)
    {
        if (onlyIfDue)
        {
            // Checked before the lock, so a pass that has nothing to do never holds the lock.
            // Otherwise a manual check arriving at that moment would wrongly get 409.
            bool isDue = await IsDueBeforeTheLockAsync(id, ct);
            if (!isDue)
            {
                return null;
            }
        }

        // A concurrent check is rejected instead of queuing another network request.
        bool gotLock = _locks.TryEnter(id);
        if (!gotLock)
        {
            throw new CheckInProgressException();
        }

        CheckOutcome? outcome;

        try
        {
            // Everything from reading the service to saving the check happens inside the lock.
            outcome = await CheckAndSaveAsync(id, onlyIfDue, ct);
        }
        finally
        {
            // In finally, so the lock is released even when the check throws or is cancelled.
            _locks.Release(id);
        }

        if (outcome == null)
        {
            return null;
        }

        // After the lock, so that a slow channel never makes an update wait or a manual check get 409.
        // After the save, so that an alarm is never sent for a check that was not saved.
        await SendAfterSaveAsync(outcome);

        return outcome.Record;
    }

    // Reads without the lock. False when the service does not exist or is not due yet.
    private async Task<bool> IsDueBeforeTheLockAsync(Guid id, CancellationToken ct)
    {
        MonitoredService? serviceBeforeLock = await _repository.GetByIdAsync(id, ct);
        if (serviceBeforeLock == null)
        {
            return false;
        }

        return IsDue(serviceBeforeLock);
    }

    // Must only be called while the lock is held. Null when the service does not exist or is not due.
    // The order of the steps matters, and each step that depends on it says why.
    private async Task<CheckOutcome?> CheckAndSaveAsync(Guid id, bool onlyIfDue, CancellationToken ct)
    {
        // Read again inside the lock: a manual check can have finished after the first read.
        MonitoredService? service = await _repository.GetByIdAsync(id, ct);
        if (service is null)
        {
            return null;
        }

        if (onlyIfDue && !IsDue(service))
        {
            return null;
        }

        // Read before the probe, so a check's time means when it started. If it meant when the
        // check ended, every response time would push the next check later.
        DateTimeOffset checkedAt = _clock.GetUtcNow();

        ProbeResult result = await _probe.CheckAsync(service.Url, service.TimeoutSeconds, ct);

        // A cancelled check says nothing about the service, so it must not be saved.
        ct.ThrowIfCancellationRequested();

        CheckRecord record = CreateCheckRecord(service, checkedAt, result);

        // Must be found before LastCheckedAt is overwritten and the check is saved below. Not inside a
        // try: a shutdown here must stop the check like any other cancellation, and nothing is saved.
        List<SecurityWarning> securityWarnings = await _securityWarningFinder.FindAsync(service, record, ct);

        // Must be read before CurrentStatus is overwritten below.
        bool becameUnknown = result.Status == ServiceStatus.Unknown && service.CurrentStatus != ServiceStatus.Unknown;

        // After the two steps above, because it overwrites what they read.
        StatusChange? statusChange = ApplyResultToService(service, record, checkedAt);

        await _repository.SaveCheckAsync(service, record, ct);

        // Worth seeing, because it can mean that the name now points to a private address.
        if (becameUnknown)
        {
            _logger.LogWarning("Service {ServiceId} changed to UNKNOWN. No notification is sent.", id);
        }

        return new CheckOutcome(record, statusChange, securityWarnings);
    }

    // Only copies what the probe saw into a new history record. Nothing is decided here.
    private static CheckRecord CreateCheckRecord(MonitoredService service, DateTimeOffset checkedAt, ProbeResult result)
    {
        CheckRecord record = new CheckRecord();
        record.Id = Guid.NewGuid();
        record.MonitoredServiceId = service.Id;
        record.Url = service.Url;
        record.CheckedAtUtc = checkedAt.UtcDateTime;
        record.Status = result.Status;
        record.StatusCode = result.StatusCode;
        record.ResponseTimeMs = result.ResponseTimeMs;
        record.FailureReason = result.FailureReason;

        // Null when no response was received, and then the three fields stay empty.
        if (result.SecurityHeaders != null)
        {
            record.SecurityGrade = result.SecurityHeaders.Grade;
            record.MissingSecurityHeaders = result.SecurityHeaders.MissingHeaders;
            record.SecurityNotAssessedReason = result.SecurityHeaders.NotAssessedReason;
        }

        // Null when nothing can be said about the certificate, and then the two fields stay empty.
        if (result.Certificate != null)
        {
            record.CertificateNotAfterUtc = result.Certificate.NotAfterUtc;
            record.CertificateNotAssessedReason = result.Certificate.NotAssessedReason;
        }

        // Null when the response said nothing about a redirect, and then the two fields stay empty.
        if (result.RedirectTarget != null)
        {
            record.RedirectFinding = result.RedirectTarget.Finding;
            record.RedirectTargetHost = result.RedirectTarget.Host;
        }

        return record;
    }

    // Updates the service with this check and returns the status change to notify about, or null.
    // The record's status and reason are the probe's own values, copied in CreateCheckRecord.
    private static StatusChange? ApplyResultToService(MonitoredService service, CheckRecord record, DateTimeOffset checkedAt)
    {
        // Saved with the check, so the row of failed checks survives a restart. Inside the lock, so a
        // manual check and the worker can never count at the same time.
        service.FailedChecksInARow = DownConfirmationRule.NextFailedChecksInARow(service.FailedChecksInARow, record.Status);

        // Null after a single failed check, or after Unknown. Then no alarm is sent, and what is
        // already known stays as it is.
        ServiceStatus? confirmedStatus = DownConfirmationRule.ConfirmedStatus(record.Status, service.FailedChecksInARow);

        StatusChange? statusChange = null;

        // Must be decided before LastKnownStatus is overwritten below.
        if (confirmedStatus != null && StatusChangeRule.ShouldNotify(service.LastKnownStatus, confirmedStatus.Value))
        {
            statusChange = new StatusChange(
                service.Id,
                service.Name,
                service.Url,
                service.LastKnownStatus,
                confirmedStatus.Value,
                checkedAt,
                record.FailureReason);
        }

        // Shows what this check saw, also when it is not confirmed yet. Only the alarm waits.
        service.CurrentStatus = record.Status;
        service.LastCheckedAt = checkedAt;

        if (confirmedStatus != null)
        {
            service.LastKnownStatus = confirmedStatus.Value;
        }

        return statusChange;
    }

    // The status message first, then the security warnings, the same order as always.
    private async Task SendAfterSaveAsync(CheckOutcome outcome)
    {
        if (outcome.StatusChange != null)
        {
            await SendNotificationAsync(outcome.StatusChange);
        }

        // Does not stop for a shutdown, the same as the message above: the check is saved, so the next
        // check sees no change, and a warning stopped here would be lost. Each send limits its own time.
        foreach (SecurityWarning warning in outcome.SecurityWarnings)
        {
            await SendSecurityWarningAsync(warning);
        }
    }

    // Catches everything on purpose: the check is already saved, so a failed notification must not
    // turn it into an error, and it must not stop the worker. Nothing here may stop the next service.
    private async Task SendNotificationAsync(StatusChange change)
    {
        try
        {
            await _notifier.NotifyStatusChangedAsync(change);
        }
        catch (NotificationFailedException exception)
        {
            // Safe to log: this exception only ever holds fixed texts.
            _logger.LogWarning("Notification failed for service {ServiceId}: {Reason}", change.ServiceId, exception.Message);
        }
        catch (Exception exception)
        {
            // Security: only the type is logged. The exception itself is not passed to the logger,
            // because its text could hold the secret address of the channel.
            _logger.LogError("Notification failed for service {ServiceId} with {ExceptionType}.", change.ServiceId, exception.GetType().Name);
        }
    }

    // Catches everything for the same reasons as SendNotificationAsync. One for each warning, so that a
    // failed warning does not stop the next one.
    private async Task SendSecurityWarningAsync(SecurityWarning warning)
    {
        try
        {
            await _notifier.NotifySecurityWarningAsync(warning);
        }
        catch (NotificationFailedException exception)
        {
            // Safe to log: this exception only ever holds fixed texts.
            _logger.LogWarning("Security warning failed for service {ServiceId}: {Reason}", warning.ServiceId, exception.Message);
        }
        catch (Exception exception)
        {
            // Security: only the type is logged, for the same reason as in SendNotificationAsync.
            _logger.LogError("Security warning failed for service {ServiceId} with {ExceptionType}.", warning.ServiceId, exception.GetType().Name);
        }
    }

    public Task<List<CheckRecord>> GetHistoryAsync(Guid id, int skip, int take, CancellationToken ct = default)
    {
        return _repository.GetHistoryAsync(id, skip, take, ct);
    }

    private bool IsDue(MonitoredService service)
    {
        if (!service.IsEnabled)
        {
            return false;
        }

        // A service that has never been checked is always due.
        if (!service.LastCheckedAt.HasValue)
        {
            return true;
        }

        DateTimeOffset nextCheckAt = service.LastCheckedAt.Value.AddSeconds(service.IntervalSeconds);
        return _clock.GetUtcNow() + DueTolerance >= nextCheckAt;
    }

    private static void Apply(MonitoredService service, ServiceConfiguration configuration)
    {
        service.Name = configuration.Name.Trim();
        service.Url = configuration.Url.Trim();
        service.IntervalSeconds = configuration.IntervalSeconds;
        service.TimeoutSeconds = configuration.TimeoutSeconds;
        service.IsEnabled = configuration.IsEnabled;
    }

    // Carries what the check found out of the lock, so that the notifications can be sent after it.
    private sealed record CheckOutcome(CheckRecord Record, StatusChange? StatusChange, List<SecurityWarning> SecurityWarnings);
}
