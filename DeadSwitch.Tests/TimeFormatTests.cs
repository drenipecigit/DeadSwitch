using System.Net.Http.Json;
using System.Text.Json;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DeadSwitch.Tests;

// These tests read the raw JSON text. Reading it into a DateTime would hide the bug,
// because a time without Z and a time with Z can parse to the same value.
public class TimeFormatTests
{
    private const string ExampleUrl = "https://example.com";

    [Fact]
    public async Task HistoryReturnsCheckTimeInUtcWithZ()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", ExampleUrl));
        HttpResponseMessage check = await client.PostAsync($"/services/{service.Id}/check", null);
        check.EnsureSuccessStatusCode();

        string json = await client.GetStringAsync($"/services/{service.Id}/history");

        Assert.Contains("\"checkedAtUtc\":\"2026-11-03T12:00:00Z\"", json);
    }

    [Fact]
    public async Task HistoryKeepsPartsOfASecondOnTheWayThroughTheDatabase()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", ExampleUrl));
        app.Clock.Advance(TimeSpan.FromMilliseconds(500));
        HttpResponseMessage check = await client.PostAsync($"/services/{service.Id}/check", null);
        check.EnsureSuccessStatusCode();

        string json = await client.GetStringAsync($"/services/{service.Id}/history");

        Assert.Contains("\"checkedAtUtc\":\"2026-11-03T12:00:00.5Z\"", json);
    }

    // The rows are inserted with SQL, because that is how rows saved by older versions look.
    [Fact]
    public async Task OldHistoryRowsGetZAndAreSortedTogetherWithNewRows()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", ExampleUrl));
        await InsertOldCheckRecordAsync(app, service.Id, "2026-01-01 12:00:00");
        await InsertOldCheckRecordAsync(app, service.Id, "2026-01-01 12:00:00.25");
        HttpResponseMessage check = await client.PostAsync($"/services/{service.Id}/check", null);
        check.EnsureSuccessStatusCode();

        JsonElement history = await client.GetFromJsonAsync<JsonElement>($"/services/{service.Id}/history");

        Assert.Equal(3, history.GetArrayLength());
        Assert.Equal("2026-11-03T12:00:00Z", history[0].GetProperty("checkedAtUtc").GetString());
        Assert.Equal("2026-01-01T12:00:00.25Z", history[1].GetProperty("checkedAtUtc").GetString());
        Assert.Equal("2026-01-01T12:00:00Z", history[2].GetProperty("checkedAtUtc").GetString());
    }

    // Reading a row must not rewrite it, otherwise the fix would really be a hidden data change.
    [Fact]
    public async Task ReadingOldHistoryLeavesItsTextInTheDatabaseUnchanged()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", ExampleUrl));
        await InsertOldCheckRecordAsync(app, service.Id, "2026-01-01 12:00:00");

        HttpResponseMessage history = await client.GetAsync($"/services/{service.Id}/history");

        history.EnsureSuccessStatusCode();
        Assert.Equal("2026-01-01 12:00:00", await ReadOnlyCheckTimeTextAsync(app));
    }

    // Old and new rows are sorted as text in the same column, so they must keep the same format.
    [Fact]
    public async Task NewCheckIsSavedWithTheSameTextFormatAsOldRows()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", ExampleUrl));

        HttpResponseMessage check = await client.PostAsync($"/services/{service.Id}/check", null);

        check.EnsureSuccessStatusCode();
        Assert.Equal("2026-11-03 12:00:00", await ReadOnlyCheckTimeTextAsync(app));
    }

    private static async Task InsertOldCheckRecordAsync(TestApp app, Guid serviceId, string checkedAt)
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        Guid recordId = Guid.NewGuid();
        string status = "UP";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO CheckRecords (Id, MonitoredServiceId, Url, CheckedAtUtc, Status, ResponseTimeMs) VALUES ({recordId}, {serviceId}, {ExampleUrl}, {checkedAt}, {status}, {12})");
    }

    private static async Task<string> ReadOnlyCheckTimeTextAsync(TestApp app)
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();

        // "AS Value" is the column name EF Core expects when a query returns plain text.
        List<string> times = await db.Database.SqlQuery<string>($"SELECT CheckedAtUtc AS Value FROM CheckRecords").ToListAsync();
        return Assert.Single(times);
    }
}
