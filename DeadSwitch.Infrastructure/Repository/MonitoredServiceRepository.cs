using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DeadSwitch.Infrastructure.Repositories;

public class MonitoredServiceRepository(DeadSwitchDbContext db) : IMonitoredServiceRepository
{
    public async Task AddAsync(MonitoredService service, CancellationToken ct = default)
    {
        db.MonitoredServices.Add(service);
        await db.SaveChangesAsync(ct);
    }
    public Task<List<MonitoredService>> GetAllAsync(CancellationToken ct = default)
        => db.MonitoredServices.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
    public Task<MonitoredService?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => db.MonitoredServices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    public async Task UpdateAsync(MonitoredService service, CancellationToken ct = default)
    {
        db.MonitoredServices.Update(service);
        await db.SaveChangesAsync(ct);
    }
    public async Task DeleteAsync(MonitoredService service, CancellationToken ct = default)
    {
        db.MonitoredServices.Remove(service);
        await db.SaveChangesAsync(ct);
    }
    public async Task SaveCheckAsync(MonitoredService service, CheckRecord record, CancellationToken ct = default)
    {
        db.MonitoredServices.Update(service);
        db.CheckRecords.Add(record);
        // One SaveChanges transaction makes status and history atomic.
        await db.SaveChangesAsync(ct);
    }
    public Task<List<CheckRecord>> GetHistoryAsync(Guid id, int skip, int take, CancellationToken ct = default)
        => db.CheckRecords.AsNoTracking().Where(x => x.MonitoredServiceId == id)
            .OrderByDescending(x => x.CheckedAtUtc).ThenByDescending(x => x.Id)
            .Skip(skip).Take(take).ToListAsync(ct);
}
