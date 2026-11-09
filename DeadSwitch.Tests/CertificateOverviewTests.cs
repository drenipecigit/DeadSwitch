using System.Net.Http.Json;
using System.Text.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

// The answer is read as raw JSON, because the dashboard depends on the exact names.
public class CertificateOverviewTests
{
    private const string FirstUrl = "https://first.example.com";
    private const string SecondUrl = "https://second.example.com";
    private const string Reason = "A reason.";

    [Fact]
    public async Task ADateGivesDaysLeftAndTextWithZ()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        DateTime notAfter = app.Clock.GetUtcNow().UtcDateTime.AddDays(14);
        app.Probe.Result = ResultWithDate(notAfter);

        await CheckAsync(client, service.Id);
        JsonElement row = await GetOnlyRowAsync(client);

        Assert.Equal(14, row.GetProperty("certificateDaysLeft").GetInt32());
        Assert.Equal("14 days left", row.GetProperty("certificateExpiryText").GetString());
        Assert.EndsWith("Z", row.GetProperty("certificateNotAfterUtc").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("certificateNotAssessedReason").ValueKind);
    }

    // Through the API with a fake clock, because no test can run the page's JavaScript.
    [Theory]
    [InlineData(-TimeSpan.TicksPerDay, -1, "Expired")]
    [InlineData(-1L, -1, "Expired")]
    [InlineData(0L, 0, "Expires today")]
    [InlineData(TimeSpan.TicksPerDay - 1, 0, "Expires today")]
    [InlineData(TimeSpan.TicksPerDay, 1, "1 day left")]
    public async Task TheLimitsAroundExpiryAreRightInTheOverview(long ticksLeft, int expectedDays, string expectedText)
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = ResultWithDate(app.Clock.GetUtcNow().UtcDateTime.AddTicks(ticksLeft));

        await CheckAsync(client, service.Id);
        JsonElement row = await GetOnlyRowAsync(client);

        Assert.Equal(expectedDays, row.GetProperty("certificateDaysLeft").GetInt32());
        Assert.Equal(expectedText, row.GetProperty("certificateExpiryText").GetString());
    }

    // The date passes without any new check, for example while the worker is turned off.
    [Fact]
    public async Task ACertificateExpiresInTheOverviewWithoutANewCheck()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = ResultWithDate(app.Clock.GetUtcNow().UtcDateTime.AddDays(1));
        await CheckAsync(client, service.Id);

        app.Clock.Advance(TimeSpan.FromDays(3));
        JsonElement row = await GetOnlyRowAsync(client);

        Assert.Equal("Expired", row.GetProperty("certificateExpiryText").GetString());
    }

    // Only the newest check counts, so an older date must never be shown after one of these.
    [Fact]
    public async Task ATimeoutHidesTheOlderDate()
    {
        ProbeResult timeout = new ProbeResult(ServiceStatus.Down, null, 10, "Check timed out.");

        JsonElement row = await GetRowAfterAGoodCheckFollowedByAsync(timeout);

        AssertNoDate(row, null);
    }

    [Fact]
    public async Task NoNewHandshakeHidesTheOlderDate()
    {
        ProbeResult noHandshake = new ProbeResult(ServiceStatus.Up, 200, 10, null, null, new CertificateCheck(null, Reason));

        JsonElement row = await GetRowAfterAGoodCheckFollowedByAsync(noHandshake);

        AssertNoDate(row, Reason);
    }

    [Fact]
    public async Task ARejectedCertificateHidesTheOlderDate()
    {
        ProbeResult rejected = new ProbeResult(ServiceStatus.Down, null, 10, "Connection, DNS or TLS failure.", null, new CertificateCheck(null, Reason));

        JsonElement row = await GetRowAfterAGoodCheckFollowedByAsync(rejected);

        AssertNoDate(row, Reason);
    }

    [Fact]
    public async Task ANewUrlEmptiesTheDate()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = ResultWithDate(app.Clock.GetUtcNow().UtcDateTime.AddDays(14));
        await CheckAsync(client, service.Id);

        var body = new { name = "Shop", url = SecondUrl, intervalSeconds = 60, timeoutSeconds = 10, isEnabled = true };
        HttpResponseMessage update = await client.PutAsJsonAsync($"/services/{service.Id}", body);
        update.EnsureSuccessStatusCode();
        JsonElement row = await GetOnlyRowAsync(client);

        AssertNoDate(row, null);
    }

    [Fact]
    public async Task AServiceThatHasNotBeenCheckedHasNoDate()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));

        JsonElement row = await GetOnlyRowAsync(client);

        AssertNoDate(row, null);
    }

    private static async Task<JsonElement> GetRowAfterAGoodCheckFollowedByAsync(ProbeResult secondResult)
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));

        app.Probe.Result = ResultWithDate(app.Clock.GetUtcNow().UtcDateTime.AddDays(14));
        await CheckAsync(client, service.Id);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        app.Probe.Result = secondResult;
        await CheckAsync(client, service.Id);

        return await GetOnlyRowAsync(client);
    }

    private static void AssertNoDate(JsonElement row, string? reason)
    {
        Assert.Equal(JsonValueKind.Null, row.GetProperty("certificateNotAfterUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("certificateDaysLeft").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("certificateExpiryText").ValueKind);
        Assert.Equal(reason, row.GetProperty("certificateNotAssessedReason").GetString());
    }

    private static ProbeResult ResultWithDate(DateTime notAfter)
    {
        return new ProbeResult(ServiceStatus.Up, 200, 12, null, null, new CertificateCheck(notAfter, null));
    }

    private static async Task<TestApp> CreateAppAsync()
    {
        TestApp app = new TestApp();
        await app.MigrateAsync();
        return app;
    }

    private static async Task CheckAsync(HttpClient client, Guid id)
    {
        HttpResponseMessage response = await client.PostAsync($"/services/{id}/check", null);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> GetOnlyRowAsync(HttpClient client)
    {
        JsonElement body = await client.GetFromJsonAsync<JsonElement>("/services/overview");
        Assert.Equal(1, body.GetArrayLength());
        return body[0];
    }
}
