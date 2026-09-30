using DeadSwitch.Domain;

namespace DeadSwitch.Application.Interfaces;

public interface IMonitoredServiceRepository
{
    Task AddAsync(MonitoredService service);
    Task <List<MonitoredService>> GetAllAsync();
}