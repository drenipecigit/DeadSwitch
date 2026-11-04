using System.Net;
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
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeadSwitch.Tests;

public class ApiTests
{
    [Fact]
    public async Task CrudCheckHistoryAndCascadeArePersistedAcrossScopes()
    {
        await using var factory = new TestApp();
        using var client = factory.CreateClient();
        await factory.MigrateAsync();
        var config = new ServiceConfiguration("Example", "https://example.com", IsEnabled: false);
        var response = await client.PostAsJsonAsync("/services", config);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<MonitoredService>())!;
        Assert.Equal($"/services/{created.Id}", response.Headers.Location!.OriginalString);
        Assert.Equal("UNKNOWN", created.CurrentStatus);
        Assert.False(created.IsEnabled);
        Assert.Single((await client.GetFromJsonAsync<List<MonitoredService>>("/services"))!);

        var check = await client.PostAsync($"/services/{created.Id}/check", null);
        check.EnsureSuccessStatusCode();
        var record = (await check.Content.ReadFromJsonAsync<CheckRecord>())!;
        Assert.Equal("UP", record.Status);
        Assert.Equal(200, record.StatusCode);
        var persisted = (await client.GetFromJsonAsync<MonitoredService>($"/services/{created.Id}"))!;
        Assert.Equal("UP", persisted.CurrentStatus);
        Assert.NotNull(persisted.LastCheckedAt);
        Assert.Equal(record.CheckedAtUtc, persisted.LastCheckedAt.Value.UtcDateTime);
        var history = (await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{created.Id}/history"))!;
        Assert.Equal(record.Id, Assert.Single(history).Id);

        factory.Clock.Now = factory.Clock.Now.AddSeconds(1);
        factory.Probe.Result = new("DOWN", 503, 31, "HTTP 503");
        (await client.PostAsync($"/services/{created.Id}/check", null)).EnsureSuccessStatusCode();
        history = (await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{created.Id}/history?take=1"))!;
        Assert.Equal("DOWN", Assert.Single(history).Status);
        Assert.Single((await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{created.Id}/history?skip=1&take=1"))!);

        var update = await client.PutAsJsonAsync($"/services/{created.Id}", config with { Url = "https://example.org", IntervalSeconds = 120 });
        update.EnsureSuccessStatusCode();
        var updated = (await update.Content.ReadFromJsonAsync<MonitoredService>())!;
        Assert.Equal("UNKNOWN", updated.CurrentStatus);
        Assert.Null(updated.LastCheckedAt);
        Assert.Equal(120, updated.IntervalSeconds);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/services/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/services/{created.Id}")).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>().CheckRecords.ToListAsync());
    }

    [Theory]
    [InlineData("", "https://example.com", 60, 10)]
    [InlineData("Example", "http://127.0.0.1", 60, 10)]
    [InlineData("Example", "https://example.com", 0, 10)]
    [InlineData("Example", "https://example.com", 60, 0)]
    public async Task InvalidConfigurationReturns400(string name, string url, int interval, int timeout)
    {
        await using var factory = new TestApp();
        using var client = factory.CreateClient();
        await factory.MigrateAsync();
        var response = await client.PostAsJsonAsync("/services", new ServiceConfiguration(name, url, interval, timeout));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<MonitoredService>>("/services"))!);
    }

    [Fact]
    public async Task MissingResourcesAndInvalidPaginationUseExpectedStatusCodes()
    {
        await using var factory = new TestApp();
        using var client = factory.CreateClient();
        await factory.MigrateAsync();
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/services/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/services/{id}/check", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/services/{id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/services/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/services/{id}", new ServiceConfiguration("X", "https://example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/services/{id}/history?take=201")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/check?url=http://127.0.0.1")).StatusCode);
    }

    [Fact]
    public async Task WorkerChecksOnlyEnabledDueServicesAndResumesAfterRestart()
    {
        await using var factory = new TestApp();
        using var client = factory.CreateClient();
        await factory.MigrateAsync();
        (await client.PostAsJsonAsync("/services", new ServiceConfiguration("Enabled", "https://example.com", IntervalSeconds: 10))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/services", new ServiceConfiguration("Paused", "https://example.org", IsEnabled: false))).EnsureSuccessStatusCode();
        var worker = factory.Worker();
        await worker.RunPassAsync(default);
        Assert.Equal(1, factory.Probe.Calls);
        await worker.RunPassAsync(default);
        Assert.Equal(1, factory.Probe.Calls);
        factory.Clock.Now = factory.Clock.Now.AddSeconds(11);
        // A fresh worker uses the database's last-check timestamp.
        using var restarted = factory.Worker();
        await restarted.RunPassAsync(default);
        Assert.Equal(2, factory.Probe.Calls);
        var services = (await client.GetFromJsonAsync<List<MonitoredService>>("/services"))!;
        Assert.Equal("UP", services.Single(x => x.IsEnabled).CurrentStatus);
        Assert.Null(services.Single(x => !x.IsEnabled).LastCheckedAt);
        worker.Dispose();
    }

    [Fact]
    public async Task OverlappingChecksReturn409AndCancellationDoesNotPersistFailure()
    {
        await using var factory = new TestApp();
        using var client = factory.CreateClient();
        await factory.MigrateAsync();
        var response = await client.PostAsJsonAsync("/services", new ServiceConfiguration("Example", "https://example.com"));
        var service = (await response.Content.ReadFromJsonAsync<MonitoredService>())!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Probe.Check = async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return factory.Probe.Result; };
        await using var scope = factory.Services.CreateAsyncScope();
        using var cancellation = new CancellationTokenSource();
        var first = scope.ServiceProvider.GetRequiredService<MonitoredServiceService>().CheckAsync(service.Id, ct: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/services/{service.Id}/check", null)).StatusCode);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Empty((await client.GetFromJsonAsync<List<CheckRecord>>($"/services/{service.Id}/history"))!);
        factory.Probe.Check = null;
        (await client.PostAsync($"/services/{service.Id}/check", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task MigrationPreservesExistingRowsAndPausesLegacyServices()
    {
        await using var factory = new TestApp();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261005062955_AddLastCheckedAtToMonitoredService");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO MonitoredServices (Id, Name, Url, CurrentStatus) VALUES ({id}, {'L' + "egacy"}, {"https://example.com"}, {"UP"})");
        await db.Database.MigrateAsync();
        var service = await db.MonitoredServices.SingleAsync();
        Assert.Equal(id, service.Id);
        Assert.Equal("UP", service.CurrentStatus);
        Assert.False(service.IsEnabled);
        Assert.Equal(60, service.IntervalSeconds);
        Assert.Equal(10, service.TimeoutSeconds);
    }
}

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class TestProbe : IHealthProbe
{
    public int Calls;
    public ProbeResult Result = new("UP", 200, 12, null);
    public Func<CancellationToken, Task<ProbeResult>>? Check;
    public Task<ProbeResult> CheckAsync(string url, int timeoutSeconds, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return Check?.Invoke(cancellationToken) ?? Task.FromResult(Result);
    }
}

internal sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _keeper;
    public TestProbe Probe { get; } = new();
    public TestClock Clock { get; } = new();
    public TestApp()
    {
        _keeper = new SqliteConnection($"Data Source=test-{Guid.NewGuid()};Mode=Memory;Cache=Shared");
        _keeper.Open();
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Monitoring:Enabled", "false");
        builder.ConfigureServices(services =>
        {
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
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>().Database.MigrateAsync();
    }
    public MonitoringWorker Worker() => new(Services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new MonitoringOptions()), NullLogger<MonitoringWorker>.Instance);
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _keeper.DisposeAsync();
    }
}
