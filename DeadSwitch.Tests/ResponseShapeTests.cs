using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

// These responses are anonymous objects, so a renamed field would still compile.
// The tests lock the exact JSON field names that clients depend on.
public class ResponseShapeTests
{
    [Fact]
    public async Task HealthReturnsStatusAndServiceName()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        string[] expectedFields = { "status", "service" };
        Assert.Equal(expectedFields, GetFieldNames(body));
        Assert.Equal("UP", body.GetProperty("status").GetString());
        Assert.Equal("DeadSwitch", body.GetProperty("service").GetString());
    }

    [Fact]
    public async Task AdHocCheckReturnsProbeResultAndIsNotCached()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/check?url=https://example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        string[] expectedFields = { "url", "status", "statusCode", "responseTimeMs", "failureReason" };
        Assert.Equal(expectedFields, GetFieldNames(body));
        Assert.Equal("https://example.com", body.GetProperty("url").GetString());
        Assert.Equal("UP", body.GetProperty("status").GetString());
        Assert.Equal(200, body.GetProperty("statusCode").GetInt32());
        Assert.Equal(12, body.GetProperty("responseTimeMs").GetInt64());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("failureReason").ValueKind);
    }

    [Fact]
    public async Task NewServiceReturnsStatusAsUnknownText()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();

        HttpResponseMessage response = await client.PostAsJsonAsync("/services", new ServiceConfiguration("Example", "https://example.com"));

        response.EnsureSuccessStatusCode();
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UNKNOWN", body.GetProperty("currentStatus").GetString());
    }

    // Reads the service and the history back afterwards, so the value has also been through the database.
    [Theory]
    [InlineData(ServiceStatus.Up, "UP")]
    [InlineData(ServiceStatus.Down, "DOWN")]
    [InlineData(ServiceStatus.Unknown, "UNKNOWN")]
    public async Task EveryCheckRouteReturnsStatusAsText(ServiceStatus status, string expectedText)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", "https://example.com"));
        app.Probe.Result = new ProbeResult(status, null, 12, null);

        HttpResponseMessage check = await client.PostAsync($"/services/{service.Id}/check", null);

        check.EnsureSuccessStatusCode();
        JsonElement checkBody = await check.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedText, checkBody.GetProperty("status").GetString());

        JsonElement serviceBody = await client.GetFromJsonAsync<JsonElement>($"/services/{service.Id}");
        Assert.Equal(expectedText, serviceBody.GetProperty("currentStatus").GetString());

        JsonElement history = await client.GetFromJsonAsync<JsonElement>($"/services/{service.Id}/history");
        Assert.Equal(expectedText, history[0].GetProperty("status").GetString());

        JsonElement adHocBody = await client.GetFromJsonAsync<JsonElement>("/check?url=https://example.com");
        Assert.Equal(expectedText, adHocBody.GetProperty("status").GetString());
    }

    private static List<string> GetFieldNames(JsonElement body)
    {
        List<string> names = new List<string>();

        foreach (JsonProperty property in body.EnumerateObject())
        {
            names.Add(property.Name);
        }

        return names;
    }
}
