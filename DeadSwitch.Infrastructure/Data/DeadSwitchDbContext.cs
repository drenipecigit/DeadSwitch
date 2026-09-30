using DeadSwitch.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeadSwitch.Infrastructure.Data;

public class DeadSwitchDbContext : DbContext
{
    public DeadSwitchDbContext(DbContextOptions<DeadSwitchDbContext> options)
        : base(options)
    {
    }

    public DbSet<MonitoredService> MonitoredServices { get; set; }
}