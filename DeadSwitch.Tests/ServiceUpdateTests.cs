using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

// PUT replaces the whole service, so every field must be sent. The bodies are written by hand,
// because a test that sends ServiceConfiguration would always send all the fields.
public class ServiceUpdateTests
{
    private const string NameField = "\"name\":\"Changed\"";
    private const string UrlField = "\"url\":\"https://example.org\"";
    private const string IntervalField = "\"intervalSeconds\":120";
    private const string TimeoutField = "\"timeoutSeconds\":20";
    private const string IsEnabledField = "\"isEnabled\":true";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateWithoutIsEnabledGives400AndLeavesTheServiceAsItWas(bool isEnabled)
    {
        string json = CreateJson(NameField, UrlField, IntervalField, TimeoutField);

        await AssertUpdateIsRejectedAsync(isEnabled, json, "IsEnabled is required when updating a service.");
    }

    [Fact]
    public async Task UpdateWithoutIntervalGives400AndLeavesTheServiceAsItWas()
    {
        string json = CreateJson(NameField, UrlField, TimeoutField, IsEnabledField);

        await AssertUpdateIsRejectedAsync(false, json, "IntervalSeconds is required when updating a service.");
    }

    [Fact]
    public async Task UpdateWithoutTimeoutGives400AndLeavesTheServiceAsItWas()
    {
        string json = CreateJson(NameField, UrlField, IntervalField, IsEnabledField);

        await AssertUpdateIsRejectedAsync(false, json, "TimeoutSeconds is required when updating a service.");
    }

    [Fact]
    public async Task UpdateWithoutNameGives400AndLeavesTheServiceAsItWas()
    {
        string json = CreateJson(UrlField, IntervalField, TimeoutField, IsEnabledField);

        await AssertUpdateIsRejectedAsync(false, json, "Name is required when updating a service.");
    }

    [Fact]
    public async Task UpdateWithoutUrlGives400AndLeavesTheServiceAsItWas()
    {
        string json = CreateJson(NameField, IntervalField, TimeoutField, IsEnabledField);

        await AssertUpdateIsRejectedAsync(false, json, "Url is required when updating a service.");
    }

    [Fact]
    public async Task IsEnabledSetToNullGives400AndLeavesTheServiceAsItWas()
    {
        string json = CreateJson(NameField, UrlField, IntervalField, TimeoutField, "\"isEnabled\":null");

        await AssertUpdateIsRejectedAsync(false, json, "IsEnabled is required when updating a service.");
    }

    // A misspelled field is treated as missing, so the mistake is not silently ignored.
    [Fact]
    public async Task MisspelledIsEnabledGives400AndLeavesTheServiceAsItWas()
    {
        string json = CreateJson(NameField, UrlField, IntervalField, TimeoutField, "\"isEnable\":false");

        await AssertUpdateIsRejectedAsync(true, json, "IsEnabled is required when updating a service.");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UpdateWithAllFieldsChangesTheService(bool enabledBefore, bool enabledAfter)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", "https://example.com", IsEnabled: enabledBefore));
        string isEnabled = $"\"isEnabled\":{enabledAfter.ToString().ToLowerInvariant()}";
        string json = CreateJson(NameField, UrlField, IntervalField, TimeoutField, isEnabled);

        HttpResponseMessage response = await client.PutAsync($"/services/{service.Id}", CreateContent(json));

        response.EnsureSuccessStatusCode();
        MonitoredService saved = (await client.GetFromJsonAsync<MonitoredService>($"/services/{service.Id}", TestJson.Options))!;
        Assert.Equal("Changed", saved.Name);
        Assert.Equal("https://example.org", saved.Url);
        Assert.Equal(120, saved.IntervalSeconds);
        Assert.Equal(20, saved.TimeoutSeconds);
        Assert.Equal(enabledAfter, saved.IsEnabled);
    }

    // A new service has no earlier value to overwrite, so POST keeps its default values.
    [Fact]
    public async Task RegisteringWithoutOptionalFieldsUsesTheDefaultValues()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        string json = CreateJson(NameField, UrlField);

        HttpResponseMessage response = await client.PostAsync("/services", CreateContent(json));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        MonitoredService created = (await response.Content.ReadFromJsonAsync<MonitoredService>(TestJson.Options))!;
        Assert.Equal(60, created.IntervalSeconds);
        Assert.Equal(10, created.TimeoutSeconds);
        Assert.True(created.IsEnabled);
    }

    private static async Task AssertUpdateIsRejectedAsync(bool enabledBefore, string json, string expectedDetail)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", "https://example.com", 300, 30, enabledBefore));
        string before = await client.GetStringAsync($"/services/{service.Id}");

        HttpResponseMessage response = await client.PutAsync($"/services/{service.Id}", CreateContent(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedDetail, body.GetProperty("detail").GetString());

        // Compares the whole saved service as text, so no field can change without being noticed.
        string after = await client.GetStringAsync($"/services/{service.Id}");
        Assert.Equal(before, after);
    }

    private static string CreateJson(params string[] fields)
    {
        return "{" + string.Join(",", fields) + "}";
    }

    private static StringContent CreateContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }
}
