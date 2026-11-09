using System.Net.Http.Json;
using System.Text.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

// The answers are read as raw JSON, because the dashboard depends on the exact names and values.
public class RedirectTargetStorageTests
{
    private const string FirstUrl = "https://first.example.com";
    private const string SecondUrl = "https://second.example.com";
    private const string Host = "other.example";

    [Fact]
    public async Task AnotherHostIsSavedAndShownInTheCheckAndTheHistory()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = TestProbeResults.RedirectToOtherHost(Host);

        JsonElement checkAnswer = await CheckAsync(client, service.Id);
        JsonElement historyRow = await GetNewestHistoryRowAsync(client, service.Id);

        AssertRedirectFields(checkAnswer, "OTHER_HOST", Host);
        AssertRedirectFields(historyRow, "OTHER_HOST", Host);
    }

    [Theory]
    [InlineData(RedirectFinding.SameHost, "SAME_HOST")]
    [InlineData(RedirectFinding.InternalTarget, "INTERNAL_TARGET")]
    [InlineData(RedirectFinding.Unreadable, "UNREADABLE")]
    public async Task AFindingWithoutAHostIsSaved(RedirectFinding finding, string expected)
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = TestProbeResults.Redirect(finding, null);

        await CheckAsync(client, service.Id);
        JsonElement historyRow = await GetNewestHistoryRowAsync(client, service.Id);

        AssertRedirectFields(historyRow, expected, null);
    }

    [Fact]
    public async Task ASuccessIsSavedAsNoRedirect()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = TestProbeResults.NoRedirect();

        await CheckAsync(client, service.Id);
        JsonElement historyRow = await GetNewestHistoryRowAsync(client, service.Id);

        AssertRedirectFields(historyRow, "NO_REDIRECT", null);
    }

    [Fact]
    public async Task ACheckWithoutAFindingLeavesBothFieldsEmpty()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = TestProbeResults.Timeout();

        await CheckAsync(client, service.Id);
        JsonElement historyRow = await GetNewestHistoryRowAsync(client, service.Id);

        AssertRedirectFields(historyRow, null, null);
    }

    [Fact]
    public async Task TheOverviewShowsTheNewestFinding()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = TestProbeResults.RedirectToOtherHost(Host);

        await CheckAsync(client, service.Id);
        JsonElement row = await GetOnlyRowAsync(client);

        AssertRedirectFields(row, "OTHER_HOST", Host);
    }

    [Fact]
    public async Task AnOlderTargetIsNotShownAfterACheckWithoutAFinding()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = TestProbeResults.RedirectToOtherHost(Host);
        await CheckAsync(client, service.Id);

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        app.Probe.Result = TestProbeResults.Timeout();
        await CheckAsync(client, service.Id);
        JsonElement row = await GetOnlyRowAsync(client);

        AssertRedirectFields(row, null, null);
    }

    [Fact]
    public async Task ANewUrlHasNoTargetInTheOverview()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = TestProbeResults.RedirectToOtherHost(Host);
        await CheckAsync(client, service.Id);

        HttpResponseMessage update = await client.PutAsJsonAsync($"/services/{service.Id}", new ServiceConfiguration("Shop", SecondUrl));
        update.EnsureSuccessStatusCode();
        JsonElement row = await GetOnlyRowAsync(client);

        AssertRedirectFields(row, null, null);
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

    private static async Task<JsonElement> GetOnlyRowAsync(HttpClient client)
    {
        JsonElement overview = await client.GetFromJsonAsync<JsonElement>("/services/overview");
        Assert.Equal(1, overview.GetArrayLength());
        return overview[0];
    }

    private static void AssertRedirectFields(JsonElement element, string? finding, string? host)
    {
        Assert.Equal(finding, element.GetProperty("redirectFinding").GetString());
        Assert.Equal(host, element.GetProperty("redirectTargetHost").GetString());
    }
}
