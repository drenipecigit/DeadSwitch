using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;

namespace DeadSwitch.Application.Services;

public class MonitoredServiceService
{
    private readonly IMonitoredServiceRepository _repository;

    public MonitoredServiceService(IMonitoredServiceRepository repository)
    {
        _repository = repository;
    }

    public async Task<MonitoredService> RegisterAsync(string name, string url)
    {
        var service = new MonitoredService
        {
            Id = Guid.NewGuid(),
            Name = name,
            Url = url
        };

        await _repository.AddAsync(service);

        return service;
    }

    public async Task<List<MonitoredService>> GetAllAsync()
    {
        return await _repository.GetAllAsync();
    }

    public async Task<MonitoredService?> GetByIdAsync(Guid id)
    {
        return await _repository.GetByIdAsync(id);
    }

    public async Task<MonitoredService?> UpdateStatusAsync(Guid id, string status)
    {
        var service = await _repository.GetByIdAsync(id);

        if (service is null)
        {
            return null;
        }

        service.CurrentStatus = status;
        service.LastCheckedAt = DateTimeOffset.UtcNow;

        await _repository.UpdateAsync(service);

        return service;
    }
}