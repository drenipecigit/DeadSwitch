using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DeadSwitch.Infrastructure.Repositories;

public class MonitoredServiceRepository : IMonitoredServiceRepository
{
    private readonly DeadSwitchDbContext _db;

    public MonitoredServiceRepository(DeadSwitchDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(MonitoredService service, CancellationToken ct = default)
    {
        _db.MonitoredServices.Add(service);
        await _db.SaveChangesAsync(ct);
    }

    // The LINQ queries in this class are not written as loops on purpose. EF Core translates
    // them to SQL, so the database does the filtering instead of loading the whole table into memory.
    public Task<List<MonitoredService>> GetAllAsync(CancellationToken ct = default)
    {
        // AsNoTracking: this data is only read, so EF Core does not need to watch it for changes.
        return _db.MonitoredServices
            .AsNoTracking()
            .OrderBy(service => service.Name)
            .ToListAsync(ct);
    }

    public Task<MonitoredService?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return _db.MonitoredServices
            .AsNoTracking()
            .FirstOrDefaultAsync(service => service.Id == id, ct);
    }

    public async Task UpdateAsync(MonitoredService service, CancellationToken ct = default)
    {
        // Update is needed because the service was read with AsNoTracking, so EF Core
        // does not know about it. Update then writes every column of the row.
        _db.MonitoredServices.Update(service);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(MonitoredService service, CancellationToken ct = default)
    {
        _db.MonitoredServices.Remove(service);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SaveCheckAsync(MonitoredService service, CheckRecord record, CancellationToken ct = default)
    {
        _db.MonitoredServices.Update(service);
        _db.CheckRecords.Add(record);

        // One SaveChanges transaction makes status and history atomic.
        await _db.SaveChangesAsync(ct);
    }

    public Task<List<CheckRecord>> GetHistoryAsync(Guid id, int skip, int take, CancellationToken ct = default)
    {
        // ThenByDescending on Id gives a stable order for two checks with the same time,
        // so a record cannot appear on two pages.
        return _db.CheckRecords
            .AsNoTracking()
            .Where(record => record.MonitoredServiceId == id)
            .OrderByDescending(record => record.CheckedAtUtc)
            .ThenByDescending(record => record.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }
}
