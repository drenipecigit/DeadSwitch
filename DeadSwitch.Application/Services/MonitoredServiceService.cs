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

}