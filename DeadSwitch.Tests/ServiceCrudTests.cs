using System.Net;
using System.Net.Http.Json;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DeadSwitch.Tests;

public class ServiceCrudTests
{
    [Fact]
    public async Task RegisteringServiceReturnsCreatedAndAddsItToTheList()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        ServiceConfiguration configuration = new ServiceConfiguration("Example", "https://example.com", IsEnabled: false);

        HttpResponseMessage response = await client.PostAsJsonAsync("/services", configuration);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        MonitoredService created = (await response.Content.ReadFromJsonAsync<MonitoredService>())!;
        Assert.Equal($"/services/{created.Id}", response.Headers.Location!.OriginalString);
        Assert.Equal("UNKNOWN", created.CurrentStatus);
        Assert.False(created.IsEnabled);
        List<MonitoredService> services = (await client.GetFromJsonAsync<List<MonitoredService>>("/services"))!;
        Assert.Single(services);
    }

    [Fact]
    public async Task ChangingUrlResetsStatusAndLastCheckedTime()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        ServiceConfiguration configuration = new ServiceConfiguration("Example", "https://example.com", IsEnabled: false);
        MonitoredService created = await app.RegisterServiceAsync(client, configuration);
        HttpResponseMessage check = await client.PostAsync($"/services/{created.Id}/check", null);
        check.EnsureSuccessStatusCode();

        ServiceConfiguration changed = configuration with { Url = "https://example.org", IntervalSeconds = 120 };
        HttpResponseMessage response = await client.PutAsJsonAsync($"/services/{created.Id}", changed);

        response.EnsureSuccessStatusCode();
        MonitoredService updated = (await response.Content.ReadFromJsonAsync<MonitoredService>())!;
        Assert.Equal("UNKNOWN", updated.CurrentStatus);
        Assert.Null(updated.LastCheckedAt);
        Assert.Equal(120, updated.IntervalSeconds);
    }

    [Fact]
    public async Task DeletingServiceAlsoDeletesItsHistory()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        ServiceConfiguration configuration = new ServiceConfiguration("Example", "https://example.com", IsEnabled: false);
        MonitoredService created = await app.RegisterServiceAsync(client, configuration);
        HttpResponseMessage check = await client.PostAsync($"/services/{created.Id}/check", null);
        check.EnsureSuccessStatusCode();

        HttpResponseMessage deleted = await client.DeleteAsync($"/services/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        HttpResponseMessage afterDelete = await client.GetAsync($"/services/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);

        // Read the table directly, because the history endpoint returns 404 for a deleted service.
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        List<CheckRecord> records = await db.CheckRecords.ToListAsync();
        Assert.Empty(records);
    }

    [Theory]
    [InlineData("", "https://example.com", 60, 10)]
    [InlineData("Example", "http://127.0.0.1", 60, 10)]
    [InlineData("Example", "https://example.com", 0, 10)]
    [InlineData("Example", "https://example.com", 60, 0)]
    public async Task InvalidConfigurationReturns400(string name, string url, int interval, int timeout)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        ServiceConfiguration configuration = new ServiceConfiguration(name, url, interval, timeout);

        HttpResponseMessage response = await client.PostAsJsonAsync("/services", configuration);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        List<MonitoredService> services = (await client.GetFromJsonAsync<List<MonitoredService>>("/services"))!;
        Assert.Empty(services);
    }

    [Fact]
    public async Task MissingServiceReturns404OnEveryRoute()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        Guid id = Guid.NewGuid();
        ServiceConfiguration configuration = new ServiceConfiguration("X", "https://example.com");

        HttpResponseMessage get = await client.GetAsync($"/services/{id}");
        HttpResponseMessage check = await client.PostAsync($"/services/{id}/check", null);
        HttpResponseMessage history = await client.GetAsync($"/services/{id}/history");
        HttpResponseMessage delete = await client.DeleteAsync($"/services/{id}");
        HttpResponseMessage update = await client.PutAsJsonAsync($"/services/{id}", configuration);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, check.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
    }

    [Fact]
    public async Task TooLargeHistoryPageReturns400()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();

        HttpResponseMessage response = await client.GetAsync($"/services/{Guid.NewGuid()}/history?take=201");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdHocCheckOfPrivateAddressReturns400()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();

        HttpResponseMessage response = await client.GetAsync("/check?url=http://127.0.0.1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
