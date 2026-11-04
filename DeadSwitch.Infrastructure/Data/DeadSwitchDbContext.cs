using DeadSwitch.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeadSwitch.Infrastructure.Data;

public class DeadSwitchDbContext(DbContextOptions<DeadSwitchDbContext> options) : DbContext(options)
{
    // Set<T>() asks EF Core for the table, so these properties need no setter of their own.
    public DbSet<MonitoredService> MonitoredServices => Set<MonitoredService>();
    public DbSet<CheckRecord> CheckRecords => Set<CheckRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MonitoredService>()
            .Property(service => service.IntervalSeconds)
            .HasDefaultValue(60);

        modelBuilder.Entity<MonitoredService>()
            .Property(service => service.TimeoutSeconds)
            .HasDefaultValue(10);

        // Existing services stay paused after the migration until explicitly enabled.
        modelBuilder.Entity<MonitoredService>()
            .Property(service => service.IsEnabled)
            .HasDefaultValue(false);

        // Stored as "UP", "DOWN" and "UNKNOWN", the same text the columns have always held, so the
        // existing rows stay valid. The default turns old NULL rows into UNKNOWN in the migration.
        modelBuilder.Entity<MonitoredService>()
            .Property(service => service.CurrentStatus)
            .HasConversion(
                status => status.ToString().ToUpperInvariant(),
                text => Enum.Parse<ServiceStatus>(text, ignoreCase: true))
            .HasDefaultValue(ServiceStatus.Unknown);

        modelBuilder.Entity<CheckRecord>()
            .Property(record => record.Status)
            .HasConversion(
                status => status.ToString().ToUpperInvariant(),
                text => Enum.Parse<ServiceStatus>(text, ignoreCase: true));

        // Cascade: deleting a service also deletes its check records in the database.
        modelBuilder.Entity<CheckRecord>()
            .HasOne<MonitoredService>()
            .WithMany()
            .HasForeignKey(record => record.MonitoredServiceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The history endpoint filters on the service and sorts on time, so this index matches it.
        modelBuilder.Entity<CheckRecord>()
            .HasIndex(record => new { record.MonitoredServiceId, record.CheckedAtUtc });
    }
}
