using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task CompatibilityCheckRouteReturnsServiceFieldsAndIsNotCached()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", "https://example.com"));

        HttpResponseMessage response = await client.GetAsync($"/services/{service.Id}/check");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        string[] expectedFields = { "id", "name", "url", "status", "statusCode", "responseTimeMs", "lastCheckedAt", "failureReason" };
        Assert.Equal(expectedFields, GetFieldNames(body));
        Assert.Equal(service.Id, body.GetProperty("id").GetGuid());
        Assert.Equal("Example", body.GetProperty("name").GetString());
        Assert.Equal("UP", body.GetProperty("status").GetString());
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
