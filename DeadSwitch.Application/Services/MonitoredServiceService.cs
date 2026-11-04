using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;

namespace DeadSwitch.Application.Services;

public class MonitoredServiceService
{
    private readonly IMonitoredServiceRepository _repository;
    private readonly IHealthProbe _probe;
    private readonly ServiceLocks _locks;

    // TimeProvider instead of DateTime.UtcNow, so the tests can control the time.
    private readonly TimeProvider _clock;

    public MonitoredServiceService(
        IMonitoredServiceRepository repository,
        IHealthProbe probe,
        ServiceLocks locks,
        TimeProvider clock)
    {
        _repository = repository;
        _probe = probe;
        _locks = locks;
        _clock = clock;
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

    public Task<MonitoredService?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return _repository.GetByIdAsync(id, ct);
    }

    public async Task<MonitoredService?> UpdateAsync(Guid id, ServiceConfiguration configuration, CancellationToken ct = default)
    {
        configuration.Validate();

        // Wait for the lock, so an update never overwrites the result of a running check.
        SemaphoreSlim gate = _locks.For(id);
        await gate.WaitAsync(ct);
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
                service.CurrentStatus = "UNKNOWN";
                service.LastCheckedAt = null;
            }

            Apply(service, configuration);
            await _repository.UpdateAsync(service, ct);
            return service;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        SemaphoreSlim gate = _locks.For(id);
        await gate.WaitAsync(ct);
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
            gate.Release();
        }
    }

    // Returns null both when the service does not exist and when onlyIfDue skips it.
    public async Task<CheckRecord?> CheckAsync(Guid id, bool onlyIfDue = false, CancellationToken ct = default)
    {
        SemaphoreSlim gate = _locks.For(id);

        // A wait of 0 means "take the lock only if it is free". A concurrent check is
        // rejected instead of queuing another network request.
        bool gotLock = await gate.WaitAsync(0, ct);
        if (!gotLock)
        {
            throw new CheckInProgressException();
        }

        try
        {
            MonitoredService? service = await _repository.GetByIdAsync(id, ct);
            if (service is null)
            {
                return null;
            }

            if (onlyIfDue && !IsDue(service))
            {
                return null;
            }

            ProbeResult result = await _probe.CheckAsync(service.Url, service.TimeoutSeconds, ct);

            // A cancelled check says nothing about the service, so it must not be saved.
            ct.ThrowIfCancellationRequested();

            DateTimeOffset checkedAt = _clock.GetUtcNow();

            CheckRecord record = new CheckRecord();
            record.Id = Guid.NewGuid();
            record.MonitoredServiceId = id;
            record.Url = service.Url;
            record.CheckedAtUtc = checkedAt.UtcDateTime;
            record.Status = result.Status;
            record.StatusCode = result.StatusCode;
            record.ResponseTimeMs = result.ResponseTimeMs;
            record.FailureReason = result.FailureReason;

            service.CurrentStatus = record.Status;
            service.LastCheckedAt = checkedAt;

            await _repository.SaveCheckAsync(service, record, ct);
            return record;
        }
        finally
        {
            // In finally, so the lock is released even when the check throws or is cancelled.
            gate.Release();
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
        return _clock.GetUtcNow() >= nextCheckAt;
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
