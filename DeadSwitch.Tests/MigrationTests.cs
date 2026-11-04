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
        Assert.Equal("UP", service.CurrentStatus);
        Assert.False(service.IsEnabled);
        Assert.Equal(60, service.IntervalSeconds);
        Assert.Equal(10, service.TimeoutSeconds);
    }
}
