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
    private readonly ILogger<MonitoredServiceService> _logger;

    public MonitoredServiceService(
        IMonitoredServiceRepository repository,
        IHealthProbe probe,
        ServiceLocks locks,
        TimeProvider clock,
        INotifier notifier,
        ILogger<MonitoredServiceService> logger)
    {
        _repository = repository;
        _probe = probe;
        _locks = locks;
        _clock = clock;
        _notifier = notifier;
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
                }
            }

            overview.Add(new ServiceOverview(
                service.Id,
                service.Name,
                service.Url,
                service.CurrentStatus,
                service.LastKnownStatus,
                service.LastCheckedAt,
                service.IsEnabled,
                responseTimeMs,
                failureReason,
                securityGrade,
                missingSecurityHeaders,
                securityNotAssessedReason));
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
    public async Task<CheckRecord?> CheckAsync(Guid id, bool onlyIfDue = false, CancellationToken ct = default)
    {
        if (onlyIfDue)
        {
            // Checked before the lock, so a pass that has nothing to do never holds the lock.
            // Otherwise a manual check arriving at that moment would wrongly get 409.
            MonitoredService? serviceBeforeLock = await _repository.GetByIdAsync(id, ct);
            if (serviceBeforeLock == null)
            {
                return null;
            }

            if (!IsDue(serviceBeforeLock))
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

        CheckRecord record;
        StatusChange? statusChange = null;

        try
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

            record = new CheckRecord();
            record.Id = Guid.NewGuid();
            record.MonitoredServiceId = id;
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

            // Must be read before CurrentStatus is overwritten below.
            bool becameUnknown = result.Status == ServiceStatus.Unknown && service.CurrentStatus != ServiceStatus.Unknown;

            // Must be decided before LastKnownStatus is overwritten below.
            if (StatusChangeRule.ShouldNotify(service.LastKnownStatus, result.Status))
            {
                statusChange = new StatusChange(
                    id,
                    service.Name,
                    service.Url,
                    service.LastKnownStatus,
                    result.Status,
                    checkedAt,
                    result.FailureReason);
            }

            service.CurrentStatus = record.Status;
            service.LastCheckedAt = checkedAt;

            // Unknown says nothing about the service, so it must not replace what is already known.
            if (result.Status != ServiceStatus.Unknown)
            {
                service.LastKnownStatus = result.Status;
            }

            await _repository.SaveCheckAsync(service, record, ct);

            // Worth seeing, because it can mean that the name now points to a private address.
            if (becameUnknown)
            {
                _logger.LogWarning("Service {ServiceId} changed to UNKNOWN. No notification is sent.", id);
            }
        }
        finally
        {
            // In finally, so the lock is released even when the check throws or is cancelled.
            _locks.Release(id);
        }

        // After the lock, so that a slow channel never makes an update wait or a manual check get 409.
        // After the save, so that an alarm is never sent for a check that was not saved.
        if (statusChange != null)
        {
            await SendNotificationAsync(statusChange);
        }

        return record;
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
}
