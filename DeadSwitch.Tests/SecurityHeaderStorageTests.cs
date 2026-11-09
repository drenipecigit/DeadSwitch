using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Monitoring;

namespace DeadSwitch.Tests;

// The answers are read as raw JSON, because the dashboard's JavaScript depends on the exact names.
public class SecurityHeaderStorageTests
{
    private const string FirstUrl = "https://first.example.com";
    private const string SecondUrl = "https://second.example.com";
    private const string MissingNames = "X-Frame-Options, Referrer-Policy";
    private const string Reason = "The response was a redirect.";

    private const string HeaderMarker = "header-marker";
    private const string LocationMarker = "location-marker";

    // Only used by the test that runs the real probe.
    private ScriptedHttpHandler? _handler;
    private HttpHealthProbe? _realProbe;

    [Fact]
    public async Task AGradeIsSavedAndShownEverywhere()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = ResultWith(new SecurityHeaderCheck("C", MissingNames, null));

        JsonElement checkAnswer = await CheckAsync(client, service.Id);
        JsonElement historyRow = await GetNewestHistoryRowAsync(client, service.Id);
        JsonElement overviewRow = await GetOnlyOverviewRowAsync(client);

        AssertSecurityFields(checkAnswer, "C", MissingNames, null);
        AssertSecurityFields(historyRow, "C", MissingNames, null);
        AssertSecurityFields(overviewRow, "C", MissingNames, null);
    }

    // An empty text and null mean different things, so the empty text must survive the database.
    [Fact]
    public async Task NothingMissingStaysAnEmptyTextAndNotNull()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = ResultWith(new SecurityHeaderCheck("A", "", null));

        await CheckAsync(client, service.Id);
        JsonElement overviewRow = await GetOnlyOverviewRowAsync(client);

        AssertSecurityFields(overviewRow, "A", "", null);
    }

    [Fact]
    public async Task AReasonIsSavedAndShownWhenThereIsNoGrade()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = ResultWith(new SecurityHeaderCheck(null, null, Reason));

        await CheckAsync(client, service.Id);
        JsonElement historyRow = await GetNewestHistoryRowAsync(client, service.Id);
        JsonElement overviewRow = await GetOnlyOverviewRowAsync(client);

        AssertSecurityFields(historyRow, null, null, Reason);
        AssertSecurityFields(overviewRow, null, null, Reason);
    }

    [Fact]
    public async Task ACheckWithoutAResponseLeavesAllThreeFieldsEmpty()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = new ProbeResult(ServiceStatus.Down, null, 10, "Check timed out.");

        await CheckAsync(client, service.Id);
        JsonElement overviewRow = await GetOnlyOverviewRowAsync(client);

        AssertSecurityFields(overviewRow, null, null, null);
    }

    [Fact]
    public async Task AServiceThatHasNotBeenCheckedHasNoSecurityValues()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));

        JsonElement overviewRow = await GetOnlyOverviewRowAsync(client);

        AssertSecurityFields(overviewRow, null, null, null);
    }

    [Fact]
    public async Task TheNewestCheckDecidesWhatTheOverviewShows()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));

        app.Probe.Result = ResultWith(new SecurityHeaderCheck("F", MissingNames, null));
        await CheckAsync(client, service.Id);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        app.Probe.Result = ResultWith(new SecurityHeaderCheck("A", "", null));
        await CheckAsync(client, service.Id);

        JsonElement overviewRow = await GetOnlyOverviewRowAsync(client);

        AssertSecurityFields(overviewRow, "A", "", null);
    }

    // The grade described the old address, so it says nothing about the new one.
    [Fact]
    public async Task ANewUrlEmptiesTheGradeInTheOverview()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = ResultWith(new SecurityHeaderCheck("A", "", null));
        await CheckAsync(client, service.Id);

        var body = new { name = "Shop", url = SecondUrl, intervalSeconds = 60, timeoutSeconds = 10, isEnabled = true };
        HttpResponseMessage update = await client.PutAsJsonAsync($"/services/{service.Id}", body);
        update.EnsureSuccessStatusCode();

        JsonElement overviewRow = await GetOnlyOverviewRowAsync(client);

        AssertSecurityFields(overviewRow, null, null, null);
    }

    // Runs the real probe against a stand-in for the network, through the API and the database. The
    // service's address is only a label here, as nothing is sent anywhere.
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Found)]
    public async Task NothingTheServerSendsReachesAnyAnswer(HttpStatusCode statusCode)
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));

        _handler = new ScriptedHttpHandler();
        _handler.Response = new HttpResponseMessage(statusCode);
        _handler.Response.Headers.Location = new Uri($"http://127.0.0.1/{LocationMarker}");
        TestSecurityHeaders.AddAll(_handler.Response.Headers, HeaderMarker);
        using HttpClient probeClient = new HttpClient(_handler);
        _realProbe = new HttpHealthProbe(probeClient, new TlsHandshakeRecorder(TimeProvider.System));
        app.Probe.Check = CheckWithTheRealProbeAsync;

        HttpResponseMessage checkResponse = await client.PostAsync($"/services/{service.Id}/check", null);
        string checkAnswer = await checkResponse.Content.ReadAsStringAsync();
        string history = await client.GetStringAsync($"/services/{service.Id}/history");
        string overview = await client.GetStringAsync("/services/overview");

        Assert.Equal(1, _handler.Requests);
        AssertHoldsNothingFromTheServer(checkAnswer);
        AssertHoldsNothingFromTheServer(history);
        AssertHoldsNothingFromTheServer(overview);
    }

    private Task<ProbeResult> CheckWithTheRealProbeAsync(CancellationToken cancellationToken)
    {
        return _realProbe!.CheckAsync(FirstUrl, 1, cancellationToken);
    }

    private static void AssertHoldsNothingFromTheServer(string answer)
    {
        Assert.DoesNotContain(HeaderMarker, answer);
        Assert.DoesNotContain(LocationMarker, answer);
    }

    private static ProbeResult ResultWith(SecurityHeaderCheck check)
    {
        return new ProbeResult(ServiceStatus.Up, 200, 12, null, check);
    }

    private static void AssertSecurityFields(JsonElement element, string? grade, string? missing, string? reason)
    {
        Assert.Equal(grade, element.GetProperty("securityGrade").GetString());
        Assert.Equal(missing, element.GetProperty("missingSecurityHeaders").GetString());
        Assert.Equal(reason, element.GetProperty("securityNotAssessedReason").GetString());
    }

    private static async Task<TestApp> CreateAppAsync()
    {
        TestApp app = new TestApp();
        await app.MigrateAsync();
        return app;
    }

    private static async Task<JsonElement> CheckAsync(HttpClient client, Guid id)
    {
        HttpResponseMessage response = await client.PostAsync($"/services/{id}/check", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> GetNewestHistoryRowAsync(HttpClient client, Guid id)
    {
        JsonElement history = await client.GetFromJsonAsync<JsonElement>($"/services/{id}/history");
        return history[0];
    }

    private static async Task<JsonElement> GetOnlyOverviewRowAsync(HttpClient client)
    {
        JsonElement body = await client.GetFromJsonAsync<JsonElement>("/services/overview");
        Assert.Equal(1, body.GetArrayLength());
        return body[0];
    }
}
