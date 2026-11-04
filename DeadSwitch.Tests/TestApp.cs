using System.Net.Http.Json;
using DeadSwitch.Api.Monitoring;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeadSwitch.Tests;

// Starts the real API in memory, with its own empty database, a fake probe and a fake clock.
internal sealed class TestApp : WebApplicationFactory<Program>
{
    // An in-memory SQLite database is deleted when its last connection closes.
    // This connection stays open so the data survives between the API's own connections.
    private readonly SqliteConnection _keeper;

    public TestProbe Probe { get; } = new TestProbe();
    public TestClock Clock { get; } = new TestClock();

    public TestApp()
    {
        // The unique name gives every test its own database.
        _keeper = new SqliteConnection($"Data Source=test-{Guid.NewGuid()};Mode=Memory;Cache=Shared");
        _keeper.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The tests run worker passes by hand, so the background loop must not run at the same time.
        builder.UseSetting("Monitoring:Enabled", "false");

        builder.ConfigureServices(services =>
        {
            // Remove the real database registration, so no test can touch the real database file.
            services.RemoveAll<DbContextOptions<DeadSwitchDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<DeadSwitchDbContext>>();
            services.AddDbContext<DeadSwitchDbContext>(options => options.UseSqlite(_keeper.ConnectionString));

            services.RemoveAll<IHealthProbe>();
            services.AddSingleton<IHealthProbe>(Probe);

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public async Task MigrateAsync()
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task<MonitoredService> RegisterServiceAsync(HttpClient client, ServiceConfiguration configuration)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/services", configuration);
        response.EnsureSuccessStatusCode();

        // The ! is safe here: the API returned success, so the body contains a service.
        MonitoredService service = (await response.Content.ReadFromJsonAsync<MonitoredService>())!;
        return service;
    }

    public MonitoringWorker CreateWorker()
    {
        IServiceScopeFactory scopes = Services.GetRequiredService<IServiceScopeFactory>();
        IOptions<MonitoringOptions> options = Options.Create(new MonitoringOptions());
        return new MonitoringWorker(scopes, options, NullLogger<MonitoringWorker>.Instance);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _keeper.DisposeAsync();
    }
}
