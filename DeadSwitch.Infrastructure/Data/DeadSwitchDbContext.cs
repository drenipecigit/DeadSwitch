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

        // The same text as CurrentStatus. EF Core never passes null to a conversion, so null stays NULL
        // and the ! is safe.
        modelBuilder.Entity<MonitoredService>()
            .Property(service => service.LastKnownStatus)
            .HasConversion(
                status => status.ToString()!.ToUpperInvariant(),
                text => Enum.Parse<ServiceStatus>(text, ignoreCase: true));

        modelBuilder.Entity<CheckRecord>()
            .Property(record => record.Status)
            .HasConversion(
                status => status.ToString().ToUpperInvariant(),
                text => Enum.Parse<ServiceStatus>(text, ignoreCase: true));

        // SQLite stores the time as text without a zone, so EF Core reads it back as Unspecified and
        // the JSON loses its Z. Everything written here is UTC, so the value is marked as UTC when read.
        modelBuilder.Entity<CheckRecord>()
            .Property(record => record.CheckedAtUtc)
            .HasConversion(
                time => time,
                stored => DateTime.SpecifyKind(stored, DateTimeKind.Utc));

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
