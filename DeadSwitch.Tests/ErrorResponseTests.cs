using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeadSwitch.Application.Services;

namespace DeadSwitch.Tests;

// Error responses are read as raw text, because anything in the body counts as a leak: also a field
// that a typed read would skip. The expected texts are written out here on purpose, so a changed
// text in the code fails a test. They are part of what a client sees.
public class ErrorResponseTests
{
    private const string SecretText = "secret-detail-from-the-exception";

    [Fact]
    public async Task UnexpectedArgumentExceptionGives500WithoutItsText()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        app.RepositoryControl.ExceptionFromGetAll = new ArgumentException(SecretText);

        HttpResponseMessage response = await client.GetAsync("/services");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SecretText, body);
    }

    [Theory]
    [InlineData("", "https://example.com", 60, 10, "Name must contain 1 to 200 characters.")]
    [InlineData("Example", "not a url", 60, 10, "Url must be an absolute HTTP(S) URL on port 80 or 443, without credentials or fragment.")]
    [InlineData("Example", "http://127.0.0.1", 60, 10, "Url must address the public Internet.")]
    [InlineData("Example", "https://example.com", 9, 10, "IntervalSeconds must be between 10 and 86400.")]
    [InlineData("Example", "https://example.com", 60, 61, "TimeoutSeconds must be between 1 and 60.")]
    public async Task ValidationErrorGivesTheExactFixedText(string name, string url, int interval, int timeout, string expectedDetail)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();

        HttpResponseMessage response = await client.PostAsJsonAsync("/services", new ServiceConfiguration(name, url, interval, timeout));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid configuration", body.GetProperty("title").GetString());
        Assert.Equal(expectedDetail, body.GetProperty("detail").GetString());
    }

    // The URL can hold anything the caller wrote, so it must never come back in the answer.
    [Theory]
    [InlineData("http://127.0.0.1/secret-path")]
    [InlineData("ftp://secret-host.example/")]
    [InlineData("https://secret-user:secret-password@example.com/")]
    public async Task ValidationErrorDoesNotEchoTheUrl(string url)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();

        HttpResponseMessage response = await client.PostAsJsonAsync("/services", new ServiceConfiguration("Example", url));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain("127.0.0.1", body);
    }

    [Fact]
    public async Task AdHocCheckOfBadUrlGivesTheExactFixedText()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/check?url=http://127.0.0.1/secret-path");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Url must address the public Internet.", body.GetProperty("detail").GetString());
    }
}
