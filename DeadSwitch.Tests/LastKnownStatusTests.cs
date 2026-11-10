using System.Net.Http.Json;
using System.Text.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

public class LastKnownStatusTests
{
    private static readonly ProbeResult Up = new ProbeResult(ServiceStatus.Up, 200, 12, null);
    private static readonly ProbeResult Down = new ProbeResult(ServiceStatus.Down, 500, 12, "HTTP 500");
    private static readonly ProbeResult Unknown = new ProbeResult(ServiceStatus.Unknown, null, 12, "URL rejected by destination policy.");

    [Fact]
    public async Task NewServiceHasNoLastKnownStatus()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();

        MonitoredService service = await app.RegisterServiceAsync(client, CreateConfiguration("https://example.com"));

        Assert.Null(service.LastKnownStatus);
    }

    [Fact]
    public async Task CheckSavesUpAndConfirmedDownAsLastKnownStatus()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, CreateConfiguration("https://example.com"));

        await CheckAsync(app, client, service.Id, Up);
        Assert.Equal(ServiceStatus.Up, await ReadLastKnownStatusAsync(client, service.Id));

        // One failed check is not confirmed yet, so the service is still known as up.
        await CheckAsync(app, client, service.Id, Down);
        Assert.Equal(ServiceStatus.Up, await ReadLastKnownStatusAsync(client, service.Id));

        await CheckAsync(app, client, service.Id, Down);
        Assert.Equal(ServiceStatus.Down, await ReadLastKnownStatusAsync(client, service.Id));
    }

    [Fact]
    public async Task UnknownDoesNotReplaceTheLastKnownStatus()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, CreateConfiguration("https://example.com"));

        await CheckAsync(app, client, service.Id, Up);
        await CheckAsync(app, client, service.Id, Unknown);

        MonitoredService? found = await client.GetFromJsonAsync<MonitoredService>($"/services/{service.Id}", TestJson.Options);
        Assert.Equal(ServiceStatus.Unknown, found!.CurrentStatus);
        Assert.Equal(ServiceStatus.Up, found.LastKnownStatus);
    }

    [Fact]
    public async Task UnknownOnANewServiceLeavesTheLastKnownStatusEmpty()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, CreateConfiguration("https://example.com"));

        await CheckAsync(app, client, service.Id, Unknown);

        Assert.Null(await ReadLastKnownStatusAsync(client, service.Id));
    }

    [Fact]
    public async Task NewUrlEmptiesTheLastKnownStatus()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, CreateConfiguration("https://example.com"));
        await CheckAsync(app, client, service.Id, Up);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/services/{service.Id}", CreateConfiguration("https://example.org"));
        response.EnsureSuccessStatusCode();

        Assert.Null(await ReadLastKnownStatusAsync(client, service.Id));
    }

    [Fact]
    public async Task NewNameKeepsTheLastKnownStatus()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, CreateConfiguration("https://example.com"));
        await CheckAsync(app, client, service.Id, Up);

        ServiceConfiguration renamed = new ServiceConfiguration("Renamed", "https://example.com");
        HttpResponseMessage response = await client.PutAsJsonAsync($"/services/{service.Id}", renamed);
        response.EnsureSuccessStatusCode();

        Assert.Equal(ServiceStatus.Up, await ReadLastKnownStatusAsync(client, service.Id));
    }

    // Read as raw JSON, because TestJson only copies the API's setting and proves nothing about it.
    [Fact]
    public async Task LastKnownStatusIsWrittenAsTextInTheJson()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, CreateConfiguration("https://example.com"));
        await CheckAsync(app, client, service.Id, Up);

        string json = await client.GetStringAsync($"/services/{service.Id}");

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("UP", document.RootElement.GetProperty("lastKnownStatus").GetString());
    }

    // A client cannot choose the status: POST and PUT read only their own fields.
    [Fact]
    public async Task LastKnownStatusCannotBeSetThroughTheApi()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, CreateConfiguration("https://example.com"));

        string body = "{\"name\":\"Same\",\"url\":\"https://example.com\",\"intervalSeconds\":60,\"timeoutSeconds\":10,\"isEnabled\":true,\"lastKnownStatus\":\"DOWN\"}";
        using StringContent content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage response = await client.PutAsync($"/services/{service.Id}", content);
        response.EnsureSuccessStatusCode();

        Assert.Null(await ReadLastKnownStatusAsync(client, service.Id));
    }

    private static ServiceConfiguration CreateConfiguration(string url)
    {
        return new ServiceConfiguration("Same", url);
    }

    private static async Task CheckAsync(TestApp app, HttpClient client, Guid id, ProbeResult result)
    {
        app.Probe.Result = result;
        HttpResponseMessage response = await client.PostAsync($"/services/{id}/check", null);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<ServiceStatus?> ReadLastKnownStatusAsync(HttpClient client, Guid id)
    {
        MonitoredService? service = await client.GetFromJsonAsync<MonitoredService>($"/services/{id}", TestJson.Options);
        return service!.LastKnownStatus;
    }
}
