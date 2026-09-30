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
    
}
