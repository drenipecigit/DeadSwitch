using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

// Passes every call on to the real repository, except where TestRepositoryControl says otherwise.
internal sealed class TestRepository : IMonitoredServiceRepository
{
    private readonly IMonitoredServiceRepository _real;
    private readonly TestRepositoryControl _control;
    private readonly ServiceLocks _locks;

    public TestRepository(IMonitoredServiceRepository real, TestRepositoryControl control, ServiceLocks locks)
    {
        _real = real;
        _control = control;
        _locks = locks;
    }

    public Task AddAsync(MonitoredService service, CancellationToken ct = default)
    {
        return _real.AddAsync(service, ct);
    }

    public Task<List<MonitoredService>> GetAllAsync(CancellationToken ct = default)
    {
        if (_control.FailGetAll)
        {
            throw new InvalidOperationException("The test made the repository fail.");
        }

        if (_control.ExceptionFromGetAll != null)
        {
            throw _control.ExceptionFromGetAll;
        }

        return _real.GetAllAsync(ct);
    }

    public async Task<MonitoredService?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        // Lets a test see whether anyone held a lock during this read.
        _control.RememberLockCount(_locks.ActiveCount);

        // The data is read before the hold, so the caller gets the values from before the hold.
        MonitoredService? service = await _real.GetByIdAsync(id, ct);
        await _control.HoldIfRequestedAsync(ct);
        return service;
    }

    public Task UpdateAsync(MonitoredService service, CancellationToken ct = default)
    {
        return _real.UpdateAsync(service, ct);
    }

    public Task DeleteAsync(MonitoredService service, CancellationToken ct = default)
    {
        return _real.DeleteAsync(service, ct);
    }

    public Task SaveCheckAsync(MonitoredService service, CheckRecord record, CancellationToken ct = default)
    {
        if (_control.FailSaveCheck)
        {
            throw new InvalidOperationException("The test made the repository fail.");
        }

        return _real.SaveCheckAsync(service, record, ct);
    }

    public Task<List<CheckRecord>> GetHistoryAsync(Guid id, int skip, int take, CancellationToken ct = default)
    {
        return _real.GetHistoryAsync(id, skip, take, ct);
    }
}
