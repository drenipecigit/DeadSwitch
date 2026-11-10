using System.Net.Http.Json;
using System.Text;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

// The status fields and the count are set by the checks only. A client that sends them in the body
// of POST or PUT must not be able to change them.
public class ServiceInputProtectionTests
{
    private const string FirstUrl = "https://example.com";

    [Fact]
    public async Task PostCannotSetTheCountOrTheStatus()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        string body = "{\"name\":\"Shop\",\"url\":\"" + FirstUrl + "\",\"failedChecksInARow\":5,\"currentStatus\":\"DOWN\",\"lastKnownStatus\":\"DOWN\"}";

        HttpResponseMessage response = await client.PostAsync("/services", CreateJson(body));
        response.EnsureSuccessStatusCode();

        MonitoredService? created = await response.Content.ReadFromJsonAsync<MonitoredService>(TestJson.Options);
        MonitoredService saved = await app.ReadSavedServiceAsync(created!.Id);
        Assert.Equal(0, saved.FailedChecksInARow);
        Assert.Equal(ServiceStatus.Unknown, saved.CurrentStatus);
        Assert.Null(saved.LastKnownStatus);
    }

    [Fact]
    public async Task PutCannotSetTheCountOrTheStatus()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", FirstUrl));
        app.Probe.Result = new ProbeResult(ServiceStatus.Up, 200, 12, null);
        HttpResponseMessage check = await client.PostAsync($"/services/{service.Id}/check", null);
        check.EnsureSuccessStatusCode();

        // The same URL, so the update itself has no reason to change the status.
        string body = "{\"name\":\"Shop\",\"url\":\"" + FirstUrl + "\",\"intervalSeconds\":60,\"timeoutSeconds\":10,\"isEnabled\":true,\"failedChecksInARow\":5,\"currentStatus\":\"DOWN\",\"lastKnownStatus\":\"DOWN\"}";
        HttpResponseMessage response = await client.PutAsync($"/services/{service.Id}", CreateJson(body));
        response.EnsureSuccessStatusCode();

        MonitoredService saved = await app.ReadSavedServiceAsync(service.Id);
        Assert.Equal(0, saved.FailedChecksInARow);
        Assert.Equal(ServiceStatus.Up, saved.CurrentStatus);
        Assert.Equal(ServiceStatus.Up, saved.LastKnownStatus);
    }

    private static async Task<TestApp> CreateAppAsync()
    {
        TestApp app = new TestApp();
        await app.MigrateAsync();
        return app;
    }

    private static StringContent CreateJson(string body)
    {
        return new StringContent(body, Encoding.UTF8, "application/json");
    }
}
