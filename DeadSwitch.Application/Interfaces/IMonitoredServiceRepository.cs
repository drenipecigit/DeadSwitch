using DeadSwitch.Domain;

namespace DeadSwitch.Application.Interfaces;

public interface IMonitoredServiceRepository
{
    Task AddAsync(MonitoredService service, CancellationToken cancellationToken = default);
    Task<List<MonitoredService>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<MonitoredService?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpdateAsync(MonitoredService service, CancellationToken cancellationToken = default);
    Task DeleteAsync(MonitoredService service, CancellationToken cancellationToken = default);
    Task SaveCheckAsync(MonitoredService service, CheckRecord record, CancellationToken cancellationToken = default);
    Task<List<CheckRecord>> GetHistoryAsync(Guid id, int skip, int take, CancellationToken cancellationToken = default);

    // The newest check of this URL where the headers were assessed, or null if there is none.
    Task<CheckRecord?> GetHeaderBaselineAsync(Guid id, string url, CancellationToken cancellationToken = default);

    // The newest check of this URL with a certificate verdict, or null if there is none.
    Task<CheckRecord?> GetCertificateBaselineAsync(Guid id, string url, CancellationToken cancellationToken = default);
}
