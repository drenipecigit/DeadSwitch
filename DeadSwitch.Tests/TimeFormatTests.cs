using System.Net.Http.Json;
using System.Text.Json;
using DeadSwitch.Api.Json;
using DeadSwitch.Application.Interfaces;
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

    // The probe moves the clock during the check. A time read after the probe would then show
    // 12:00:00.5, so this test also holds the contract that a check's time is when it started.
    [Fact]
    public async Task EveryRouteWritesTheStartOfTheCheckInUtcWithZ()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        ServiceConfiguration configuration = new ServiceConfiguration("Example", ExampleUrl);

        HttpResponseMessage created = await client.PostAsJsonAsync("/services", configuration);
        string createdJson = await created.Content.ReadAsStringAsync();
        Assert.Contains("\"lastCheckedAt\":null", createdJson);
        MonitoredService service = JsonSerializer.Deserialize<MonitoredService>(createdJson, TestJson.Options)!;

        Task<ProbeResult> MoveClockDuringCheck(CancellationToken cancellationToken)
        {
            app.Clock.Advance(TimeSpan.FromMilliseconds(500));
            return Task.FromResult(new ProbeResult(ServiceStatus.Up, 200, 500, null));
        }

        app.Probe.Check = MoveClockDuringCheck;
        string expectedCheckedAt = "\"checkedAtUtc\":\"2026-11-03T12:00:00Z\"";
        string expectedLastCheckedAt = "\"lastCheckedAt\":\"2026-11-03T12:00:00Z\"";

        HttpResponseMessage check = await client.PostAsync($"/services/{service.Id}/check", null);
        string checkJson = await check.Content.ReadAsStringAsync();
        Assert.Contains(expectedCheckedAt, checkJson);

        string historyJson = await client.GetStringAsync($"/services/{service.Id}/history");
        Assert.Contains(expectedCheckedAt, historyJson);

        string listJson = await client.GetStringAsync("/services");
        Assert.Contains(expectedLastCheckedAt, listJson);

        string serviceJson = await client.GetStringAsync($"/services/{service.Id}");
        Assert.Contains(expectedLastCheckedAt, serviceJson);

        // The URL is unchanged, so the update keeps the time of the last check.
        HttpResponseMessage update = await client.PutAsJsonAsync($"/services/{service.Id}", configuration);
        string updateJson = await update.Content.ReadAsStringAsync();
        Assert.Contains(expectedLastCheckedAt, updateJson);
    }

    // +01:00 has never been written by the application. It is tested anyway, because it shows that
    // the time is converted to the same moment and that the sign is not just replaced by a Z.
    [Theory]
    [InlineData("2026-11-03 12:00:00+00:00", "2026-11-03T12:00:00Z")]
    [InlineData("2026-11-03 12:00:00.25+00:00", "2026-11-03T12:00:00.25Z")]
    [InlineData("2026-11-03 13:00:00+01:00", "2026-11-03T12:00:00Z")]
    public async Task SavedLastCheckedTimeIsWrittenInUtcWithZ(string savedText, string expectedText)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", ExampleUrl));
        await SetLastCheckedTextAsync(app, service.Id, savedText);

        string json = await client.GetStringAsync($"/services/{service.Id}");

        Assert.Contains($"\"lastCheckedAt\":\"{expectedText}\"", json);
    }

    // No request contains a time today, so no other test reaches the Read method of the converter.
    [Fact]
    public void ConverterReadsATimeBackAsTheSameMoment()
    {
        JsonSerializerOptions options = new JsonSerializerOptions();
        options.Converters.Add(new UtcDateTimeOffsetConverter());

        DateTimeOffset time = JsonSerializer.Deserialize<DateTimeOffset>("\"2026-11-03T13:00:00+01:00\"", options);

        Assert.Equal(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero), time);
    }

    private static async Task SetLastCheckedTextAsync(TestApp app, Guid serviceId, string lastCheckedAt)
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE MonitoredServices SET LastCheckedAt = {lastCheckedAt} WHERE Id = {serviceId}");
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
