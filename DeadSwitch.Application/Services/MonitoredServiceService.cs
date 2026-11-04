using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;

namespace DeadSwitch.Application.Services;

public class MonitoredServiceService(
    IMonitoredServiceRepository repository, IHealthProbe probe, ServiceLocks locks, TimeProvider clock)
{
    public async Task<MonitoredService> RegisterAsync(ServiceConfiguration configuration, CancellationToken ct = default)
    {
        configuration.Validate();
        var service = new MonitoredService { Id = Guid.NewGuid() };
        Apply(service, configuration);
        await repository.AddAsync(service, ct);
        return service;
    }

    public Task<List<MonitoredService>> GetAllAsync(CancellationToken ct = default) => repository.GetAllAsync(ct);
    public Task<MonitoredService?> GetByIdAsync(Guid id, CancellationToken ct = default) => repository.GetByIdAsync(id, ct);

    public async Task<MonitoredService?> UpdateAsync(Guid id, ServiceConfiguration configuration, CancellationToken ct = default)
    {
        configuration.Validate();
        var gate = locks.For(id);
        await gate.WaitAsync(ct);
        try
        {
            var service = await repository.GetByIdAsync(id, ct);
            if (service is null) return null;
            if (service.Url != configuration.Url.Trim())
            {
                service.CurrentStatus = "UNKNOWN";
                service.LastCheckedAt = null;
            }
            Apply(service, configuration);
            await repository.UpdateAsync(service, ct);
            return service;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var gate = locks.For(id);
        await gate.WaitAsync(ct);
        try
        {
            var service = await repository.GetByIdAsync(id, ct);
            if (service is null) return false;
            await repository.DeleteAsync(service, ct);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task<CheckRecord?> CheckAsync(Guid id, bool onlyIfDue = false, CancellationToken ct = default)
    {
        var gate = locks.For(id);
        // A concurrent check is rejected instead of queuing another network request.
        if (!await gate.WaitAsync(0, ct)) throw new CheckInProgressException();
        try
        {
            var service = await repository.GetByIdAsync(id, ct);
            if (service is null) return null;
            var now = clock.GetUtcNow();
            if (onlyIfDue && (!service.IsEnabled ||
                (service.LastCheckedAt.HasValue && now < service.LastCheckedAt.Value.AddSeconds(service.IntervalSeconds))))
                return null;

            var result = await probe.CheckAsync(service.Url, service.TimeoutSeconds, ct);
            ct.ThrowIfCancellationRequested();
            var checkedAt = clock.GetUtcNow();
            var record = new CheckRecord
            {
                Id = Guid.NewGuid(), MonitoredServiceId = id, Url = service.Url,
                CheckedAtUtc = checkedAt.UtcDateTime, Status = result.Status,
                StatusCode = result.StatusCode, ResponseTimeMs = result.ResponseTimeMs,
                FailureReason = result.FailureReason
            };
            service.CurrentStatus = record.Status;
            service.LastCheckedAt = checkedAt;
            await repository.SaveCheckAsync(service, record, ct);
            return record;
        }
        finally { gate.Release(); }
    }

    public Task<List<CheckRecord>> GetHistoryAsync(Guid id, int skip, int take, CancellationToken ct = default)
        => repository.GetHistoryAsync(id, skip, take, ct);

    private static void Apply(MonitoredService service, ServiceConfiguration configuration)
    {
        service.Name = configuration.Name.Trim();
        service.Url = configuration.Url.Trim();
        service.IntervalSeconds = configuration.IntervalSeconds;
        service.TimeoutSeconds = configuration.TimeoutSeconds;
        service.IsEnabled = configuration.IsEnabled;
    }
}

public sealed class CheckInProgressException : Exception
{
    public CheckInProgressException() : base("A check or configuration change is already in progress. Retry later.") { }
}
