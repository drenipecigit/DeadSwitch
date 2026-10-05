using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DeadSwitch.Infrastructure.Repositories;

public class MonitoredServiceRepository : IMonitoredServiceRepository
{
    private readonly DeadSwitchDbContext _dbContext;

    public MonitoredServiceRepository(DeadSwitchDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(MonitoredService service)
    {
        _dbContext.MonitoredServices.Add(service);
        await _dbContext.SaveChangesAsync();
    }

    public async Task<List<MonitoredService>> GetAllAsync()
    {
        return await _dbContext.MonitoredServices.ToListAsync();
    }

    public async Task<MonitoredService?> GetByIdAsync(Guid id)
    {
        return await _dbContext.MonitoredServices
            .FirstOrDefaultAsync(service => service.Id == id);
    }

    public async Task UpdateAsync(MonitoredService service)
    {
        _dbContext.MonitoredServices.Update(service);
        await _dbContext.SaveChangesAsync();
    }
}