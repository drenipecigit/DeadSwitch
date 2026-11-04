using DeadSwitch.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeadSwitch.Infrastructure.Data;

public class DeadSwitchDbContext(DbContextOptions<DeadSwitchDbContext> options) : DbContext(options)
{
    public DbSet<MonitoredService> MonitoredServices => Set<MonitoredService>();
    public DbSet<CheckRecord> CheckRecords => Set<CheckRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MonitoredService>().Property(x => x.IntervalSeconds).HasDefaultValue(60);
        modelBuilder.Entity<MonitoredService>().Property(x => x.TimeoutSeconds).HasDefaultValue(10);
        // Existing services stay paused after the migration until explicitly enabled.
        modelBuilder.Entity<MonitoredService>().Property(x => x.IsEnabled).HasDefaultValue(false);
        modelBuilder.Entity<CheckRecord>().HasOne<MonitoredService>().WithMany()
            .HasForeignKey(x => x.MonitoredServiceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CheckRecord>().HasIndex(x => new { x.MonitoredServiceId, x.CheckedAtUtc });
    }
}
