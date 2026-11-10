using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DeadSwitch.Tests;

public class MigrationTests
{
    [Fact]
    public async Task MigrationPreservesExistingRowsAndPausesOldServices()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        IMigrator migrator = db.GetService<IMigrator>();

        // Stop at the migration before the one under test, and insert a row as the old schema looked.
        await migrator.MigrateAsync("20261005062955_AddLastCheckedAtToMonitoredService");
        Guid id = Guid.NewGuid();
        string name = "Legacy";
        string url = "https://example.com";
        string status = "UP";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO MonitoredServices (Id, Name, Url, CurrentStatus) VALUES ({id}, {name}, {url}, {status})");

        await db.Database.MigrateAsync();

        MonitoredService service = await db.MonitoredServices.SingleAsync();
        Assert.Equal(id, service.Id);
        Assert.Equal(ServiceStatus.Up, service.CurrentStatus);
        Assert.False(service.IsEnabled);
        Assert.Equal(60, service.IntervalSeconds);
        Assert.Equal(10, service.TimeoutSeconds);
    }

    // SQLite rebuilds the whole table for this migration. Dropping the old table with foreign keys
    // turned on would delete every check record through the cascade, so that is tested here.
    [Fact]
    public async Task RequiredStatusMigrationFillsMissingStatusAndKeepsHistory()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        IMigrator migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261103133338_AddMonitoringHistoryAndScheduling");
        Guid missingStatusId = Guid.NewGuid();
        Guid upId = Guid.NewGuid();
        Guid recordId = Guid.NewGuid();
        string url = "https://example.com";
        string status = "UP";
        string checkedAt = "2026-01-01 12:00:00";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO MonitoredServices (Id, Name, Url, CurrentStatus) VALUES ({missingStatusId}, {"Missing"}, {url}, NULL)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO MonitoredServices (Id, Name, Url, CurrentStatus) VALUES ({upId}, {"Up"}, {url}, {status})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO CheckRecords (Id, MonitoredServiceId, Url, CheckedAtUtc, Status, ResponseTimeMs) VALUES ({recordId}, {upId}, {url}, {checkedAt}, {status}, {12})");

        await db.Database.MigrateAsync();

        MonitoredService missingStatus = await db.MonitoredServices.SingleAsync(service => service.Id == missingStatusId);
        MonitoredService up = await db.MonitoredServices.SingleAsync(service => service.Id == upId);
        CheckRecord record = await db.CheckRecords.SingleAsync();
        Assert.Equal(ServiceStatus.Unknown, missingStatus.CurrentStatus);
        Assert.Equal(ServiceStatus.Up, up.CurrentStatus);
        Assert.Equal(recordId, record.Id);
        Assert.Equal(ServiceStatus.Up, record.Status);
    }

    [Fact]
    public async Task LastKnownStatusMigrationCopiesUpAndDownAndKeepsHistory()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        IMigrator migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261104105248_MakeCurrentStatusRequired");
        Guid upId = Guid.NewGuid();
        Guid downId = Guid.NewGuid();
        Guid unknownId = Guid.NewGuid();
        Guid recordId = Guid.NewGuid();
        string url = "https://example.com";
        string up = "UP";
        string down = "DOWN";
        string unknown = "UNKNOWN";
        string checkedAt = "2026-01-01 12:00:00";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO MonitoredServices (Id, Name, Url, CurrentStatus) VALUES ({upId}, {"Up"}, {url}, {up})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO MonitoredServices (Id, Name, Url, CurrentStatus) VALUES ({downId}, {"Down"}, {url}, {down})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO MonitoredServices (Id, Name, Url, CurrentStatus) VALUES ({unknownId}, {"Unknown"}, {url}, {unknown})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO CheckRecords (Id, MonitoredServiceId, Url, CheckedAtUtc, Status, ResponseTimeMs) VALUES ({recordId}, {upId}, {url}, {checkedAt}, {up}, {12})");

        await db.Database.MigrateAsync();

        MonitoredService upService = await db.MonitoredServices.SingleAsync(service => service.Id == upId);
        MonitoredService downService = await db.MonitoredServices.SingleAsync(service => service.Id == downId);
        MonitoredService unknownService = await db.MonitoredServices.SingleAsync(service => service.Id == unknownId);
        CheckRecord record = await db.CheckRecords.SingleAsync();
        Assert.Equal(ServiceStatus.Up, upService.LastKnownStatus);
        Assert.Equal(ServiceStatus.Down, downService.LastKnownStatus);
        Assert.Null(unknownService.LastKnownStatus);
        Assert.Equal(recordId, record.Id);
    }

    [Fact]
    public async Task SecurityHeaderMigrationKeepsOldRecordsWithoutAGrade()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        IMigrator migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261105074323_AddLastKnownStatusToMonitoredService");
        Guid serviceId = Guid.NewGuid();
        Guid recordId = Guid.NewGuid();
        string url = "https://example.com";
        string up = "UP";
        string checkedAt = "2026-01-01 12:00:00";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO MonitoredServices (Id, Name, Url, CurrentStatus) VALUES ({serviceId}, {"Old"}, {url}, {up})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO CheckRecords (Id, MonitoredServiceId, Url, CheckedAtUtc, Status, ResponseTimeMs) VALUES ({recordId}, {serviceId}, {url}, {checkedAt}, {up}, {12})");

        await db.Database.MigrateAsync();

        CheckRecord record = await db.CheckRecords.SingleAsync();
        Assert.Equal(recordId, record.Id);
        Assert.Equal(ServiceStatus.Up, record.Status);
        Assert.Null(record.SecurityGrade);
        Assert.Null(record.MissingSecurityHeaders);
        Assert.Null(record.SecurityNotAssessedReason);
    }

    [Fact]
    public async Task CertificateMigrationKeepsOldRecordsWithoutADate()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        IMigrator migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261109065939_AddSecurityHeadersToCheckRecords");
        Guid serviceId = Guid.NewGuid();
        Guid recordId = Guid.NewGuid();
        string url = "https://example.com";
        string up = "UP";
        string checkedAt = "2026-01-01 12:00:00";
        string grade = "B";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO MonitoredServices (Id, Name, Url, CurrentStatus) VALUES ({serviceId}, {"Old"}, {url}, {up})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO CheckRecords (Id, MonitoredServiceId, Url, CheckedAtUtc, Status, ResponseTimeMs, SecurityGrade) VALUES ({recordId}, {serviceId}, {url}, {checkedAt}, {up}, {12}, {grade})");

        await db.Database.MigrateAsync();

        CheckRecord record = await db.CheckRecords.SingleAsync();
        Assert.Equal(recordId, record.Id);
        Assert.Equal("B", record.SecurityGrade);
        Assert.Null(record.CertificateNotAfterUtc);
        Assert.Null(record.CertificateNotAssessedReason);
    }

    [Fact]
    public async Task RedirectMigrationKeepsOldRecordsWithoutAFinding()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        IMigrator migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261109095631_AddCertificateToCheckRecords");
        Guid serviceId = Guid.NewGuid();
        Guid recordId = Guid.NewGuid();
        string url = "https://example.com";
        string up = "UP";
        string checkedAt = "2026-01-01 12:00:00";
        string grade = "B";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO MonitoredServices (Id, Name, Url, CurrentStatus) VALUES ({serviceId}, {"Old"}, {url}, {up})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO CheckRecords (Id, MonitoredServiceId, Url, CheckedAtUtc, Status, ResponseTimeMs, SecurityGrade) VALUES ({recordId}, {serviceId}, {url}, {checkedAt}, {up}, {12}, {grade})");

        await db.Database.MigrateAsync();

        CheckRecord record = await db.CheckRecords.SingleAsync();
        Assert.Equal(recordId, record.Id);
        Assert.Equal("B", record.SecurityGrade);
        Assert.Null(record.RedirectFinding);
        Assert.Null(record.RedirectTargetHost);
    }

    [Fact]
    public async Task FailedChecksMigrationGivesZeroToOldRows()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        IMigrator migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261109134809_AddRedirectTargetToCheckRecords");
        Guid serviceId = Guid.NewGuid();
        string url = "https://example.com";
        string up = "UP";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO MonitoredServices (Id, Name, Url, CurrentStatus, LastKnownStatus) VALUES ({serviceId}, {"Old"}, {url}, {up}, {up})");

        await db.Database.MigrateAsync();

        MonitoredService service = await db.MonitoredServices.SingleAsync();
        Assert.Equal(serviceId, service.Id);
        Assert.Equal(ServiceStatus.Up, service.LastKnownStatus);
        Assert.Equal(0, service.FailedChecksInARow);
    }

    // Fails if the model was changed without a matching migration.
    [Fact]
    public async Task ModelMatchesTheMigrations()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();

        Assert.False(db.Database.HasPendingModelChanges());
    }
}
