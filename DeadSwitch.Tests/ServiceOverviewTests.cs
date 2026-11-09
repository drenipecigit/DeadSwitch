using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

// The answer is read as raw JSON, because the dashboard's JavaScript depends on the exact names.
public class ServiceOverviewTests
{
    private const string FirstUrl = "https://first.example.com";
    private const string SecondUrl = "https://second.example.com";

    [Fact]
    public async Task WithoutServicesTheListIsEmpty()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/services/overview");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetArrayLength());
    }

    [Fact]
    public async Task ARowHasExactlyTheFieldsTheDashboardReads()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));

        JsonElement row = await GetOnlyRowAsync(client);

        List<string> names = new List<string>();
        foreach (JsonProperty property in row.EnumerateObject())
        {
            names.Add(property.Name);
        }

        string[] expected = { "id", "name", "url", "currentStatus", "lastKnownStatus", "lastCheckedAt", "isEnabled", "responseTimeMs", "failureReason", "securityGrade", "missingSecurityHeaders", "securityNotAssessedReason", "certificateNotAfterUtc", "certificateDaysLeft", "certificateExpiryText", "certificateNotAssessedReason", "redirectFinding", "redirectTargetHost" };
        Assert.Equal(expected, names);
    }

    [Fact]
    public async Task ANewServiceHasNoCheckValues()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));

        JsonElement row = await GetOnlyRowAsync(client);

        Assert.Equal("Shop", row.GetProperty("name").GetString());
        Assert.Equal(FirstUrl, row.GetProperty("url").GetString());
        Assert.Equal("UNKNOWN", row.GetProperty("currentStatus").GetString());
        Assert.True(row.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("lastKnownStatus").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("lastCheckedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("responseTimeMs").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("failureReason").ValueKind);
    }

    [Fact]
    public async Task ACheckGivesItsStatusResponseTimeAndTimeWithZ()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = new ProbeResult(ServiceStatus.Up, 200, 123, null);

        await CheckAsync(client, service.Id);
        JsonElement row = await GetOnlyRowAsync(client);

        Assert.Equal("UP", row.GetProperty("currentStatus").GetString());
        Assert.Equal("UP", row.GetProperty("lastKnownStatus").GetString());
        Assert.Equal(123, row.GetProperty("responseTimeMs").GetInt64());
        Assert.Equal("2026-11-03T12:00:00Z", row.GetProperty("lastCheckedAt").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("failureReason").ValueKind);
    }

    [Fact]
    public async Task AFailedCheckGivesItsFailureReason()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = new ProbeResult(ServiceStatus.Down, 500, 45, "HTTP 500");

        await CheckAsync(client, service.Id);
        JsonElement row = await GetOnlyRowAsync(client);

        Assert.Equal("DOWN", row.GetProperty("currentStatus").GetString());
        Assert.Equal(45, row.GetProperty("responseTimeMs").GetInt64());
        Assert.Equal("HTTP 500", row.GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task TheNewestCheckIsTheOneShown()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));

        app.Probe.Result = new ProbeResult(ServiceStatus.Down, 500, 111, "HTTP 500");
        await CheckAsync(client, service.Id);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        app.Probe.Result = new ProbeResult(ServiceStatus.Up, 200, 222, null);
        await CheckAsync(client, service.Id);

        JsonElement row = await GetOnlyRowAsync(client);

        Assert.Equal(222, row.GetProperty("responseTimeMs").GetInt64());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("failureReason").ValueKind);
    }

    // The history is kept when the URL changes, but its latest record describes the old address.
    [Fact]
    public async Task ANewUrlDoesNotShowTheOldUrlsResult()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = new ProbeResult(ServiceStatus.Down, 500, 45, "HTTP 500");
        await CheckAsync(client, service.Id);

        await UpdateAsync(client, service.Id, "Shop", SecondUrl);
        JsonElement row = await GetOnlyRowAsync(client);

        Assert.Equal(SecondUrl, row.GetProperty("url").GetString());
        Assert.Equal("UNKNOWN", row.GetProperty("currentStatus").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("lastCheckedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("responseTimeMs").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("failureReason").ValueKind);
    }

    // Comparing the record's URL with the service's would be fooled here: the old record has the same URL.
    [Fact]
    public async Task GoingBackToAnOldUrlDoesNotShowItsOldResult()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = new ProbeResult(ServiceStatus.Down, 500, 45, "HTTP 500");
        await CheckAsync(client, service.Id);

        await UpdateAsync(client, service.Id, "Shop", SecondUrl);
        await UpdateAsync(client, service.Id, "Shop", FirstUrl);
        JsonElement row = await GetOnlyRowAsync(client);

        Assert.Equal(JsonValueKind.Null, row.GetProperty("lastCheckedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("responseTimeMs").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("failureReason").ValueKind);
    }

    [Fact]
    public async Task ACheckOfTheNewUrlIsShown()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = new ProbeResult(ServiceStatus.Down, 500, 45, "HTTP 500");
        await CheckAsync(client, service.Id);
        await UpdateAsync(client, service.Id, "Shop", SecondUrl);

        app.Clock.Advance(TimeSpan.FromMinutes(1));
        app.Probe.Result = new ProbeResult(ServiceStatus.Up, 200, 77, null);
        await CheckAsync(client, service.Id);
        JsonElement row = await GetOnlyRowAsync(client);

        Assert.Equal(77, row.GetProperty("responseTimeMs").GetInt64());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("failureReason").ValueKind);
    }

    [Fact]
    public async Task ANewNameKeepsTheResult()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = new ProbeResult(ServiceStatus.Up, 200, 88, null);
        await CheckAsync(client, service.Id);

        await UpdateAsync(client, service.Id, "New name", FirstUrl);
        JsonElement row = await GetOnlyRowAsync(client);

        Assert.Equal("New name", row.GetProperty("name").GetString());
        Assert.Equal(88, row.GetProperty("responseTimeMs").GetInt64());
    }

    [Fact]
    public async Task EveryServiceGetsItsOwnResult()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService first = await app.RegisterServiceAsync(client, new ServiceConfiguration("A first", FirstUrl));
        MonitoredService second = await app.RegisterServiceAsync(client, new ServiceConfiguration("B second", SecondUrl));

        app.Probe.Result = new ProbeResult(ServiceStatus.Up, 200, 11, null);
        await CheckAsync(client, first.Id);
        app.Probe.Result = new ProbeResult(ServiceStatus.Down, 503, 22, "HTTP 503");
        await CheckAsync(client, second.Id);

        JsonElement body = await client.GetFromJsonAsync<JsonElement>("/services/overview");

        Assert.Equal(2, body.GetArrayLength());
        Assert.Equal(first.Id.ToString(), body[0].GetProperty("id").GetString());
        Assert.Equal(11, body[0].GetProperty("responseTimeMs").GetInt64());
        Assert.Equal(second.Id.ToString(), body[1].GetProperty("id").GetString());
        Assert.Equal(22, body[1].GetProperty("responseTimeMs").GetInt64());
        Assert.Equal("HTTP 503", body[1].GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task APausedServiceIsMarkedAsNotEnabled()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl, IsEnabled: false));

        JsonElement row = await GetOnlyRowAsync(client);

        Assert.False(row.GetProperty("isEnabled").GetBoolean());
    }

    // The dashboard calls this route every few seconds, so it must never start a check.
    [Fact]
    public async Task ReadingTheOverviewStartsNoCheckAndChangesNothing()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));

        await client.GetAsync("/services/overview");
        await client.GetAsync("/services/overview");

        Assert.Equal(0, app.Probe.Calls);
        JsonElement history = await client.GetFromJsonAsync<JsonElement>($"/services/{service.Id}/history");
        Assert.Equal(0, history.GetArrayLength());
    }

    // The new fixed route sits next to {id:guid}, and must not take over from it or from the list.
    [Fact]
    public async Task TheOtherServiceRoutesWorkAsBefore()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));

        HttpResponseMessage one = await client.GetAsync($"/services/{service.Id}");
        HttpResponseMessage all = await client.GetAsync("/services");
        HttpResponseMessage notAGuid = await client.GetAsync("/services/not-a-guid");

        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notAGuid.StatusCode);
    }

    private static async Task<TestApp> CreateAppAsync()
    {
        TestApp app = new TestApp();
        await app.MigrateAsync();
        return app;
    }

    private static async Task<JsonElement> GetOnlyRowAsync(HttpClient client)
    {
        JsonElement body = await client.GetFromJsonAsync<JsonElement>("/services/overview");
        Assert.Equal(1, body.GetArrayLength());
        return body[0];
    }

    private static async Task CheckAsync(HttpClient client, Guid id)
    {
        HttpResponseMessage response = await client.PostAsync($"/services/{id}/check", null);
        response.EnsureSuccessStatusCode();
    }

    private static async Task UpdateAsync(HttpClient client, Guid id, string name, string url)
    {
        var body = new { name, url, intervalSeconds = 60, timeoutSeconds = 10, isEnabled = true };
        HttpResponseMessage response = await client.PutAsJsonAsync($"/services/{id}", body);
        response.EnsureSuccessStatusCode();
    }
}
