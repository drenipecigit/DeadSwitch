using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DeadSwitch.Application.Services;
using Microsoft.AspNetCore.Http;

namespace DeadSwitch.Tests;

// Error responses are read as raw text, because anything in the body counts as a leak: also a field
// that a typed read would skip. The expected texts are written out here on purpose, so a changed
// text in the code fails a test. They are part of what a client sees.
public class ErrorResponseTests
{
    private const string SecretText = "secret-detail-from-the-exception";
    private const string Development = "Development";
    private const string Production = "Production";

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

    [Theory]
    [InlineData(Development, "{bad")]
    [InlineData(Production, "{bad")]
    [InlineData(Development, "")]
    [InlineData(Production, "")]
    [InlineData(Development, "null")]
    [InlineData(Production, "null")]
    [InlineData(Development, "{\"name\":\"Example\",\"url\":\"https://example.com\",\"intervalSeconds\":\"secret-value\"}")]
    [InlineData(Production, "{\"name\":\"Example\",\"url\":\"https://example.com\",\"intervalSeconds\":\"secret-value\"}")]
    [InlineData(Development, "{\"name\":\"Example\",\"url\":\"https://example.com\",\"intervalSeconds\":99999999999}")]
    [InlineData(Production, "{\"name\":\"Example\",\"url\":\"https://example.com\",\"intervalSeconds\":99999999999}")]
    public async Task UnreadableJsonGivesTheFixed400AndSavesNothing(string environment, string json)
    {
        await using TestApp app = new TestApp(environment);
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();

        HttpResponseMessage response = await client.PostAsync("/services", CreateJsonContent(json));

        await AssertIsFixedUnreadableAnswerAsync(HttpStatusCode.BadRequest, response);
        JsonElement services = await client.GetFromJsonAsync<JsonElement>("/services");
        Assert.Equal(0, services.GetArrayLength());
    }

    [Theory]
    [InlineData(Development)]
    [InlineData(Production)]
    public async Task UnreadableJsonOnUpdateGivesTheFixed400(string environment)
    {
        await using TestApp app = new TestApp(environment);
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();

        HttpResponseMessage response = await client.PutAsync($"/services/{Guid.NewGuid()}", CreateJsonContent("{bad"));

        await AssertIsFixedUnreadableAnswerAsync(HttpStatusCode.BadRequest, response);
    }

    [Theory]
    [InlineData(Development)]
    [InlineData(Production)]
    public async Task WrongContentTypeGivesTheFixed415(string environment)
    {
        await using TestApp app = new TestApp(environment);
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        StringContent content = new StringContent("secret-value", Encoding.UTF8, "text/plain");

        HttpResponseMessage response = await client.PostAsync("/services", content);

        await AssertIsFixedUnreadableAnswerAsync(HttpStatusCode.UnsupportedMediaType, response);
    }

    [Theory]
    [InlineData(Development)]
    [InlineData(Production)]
    public async Task AdHocCheckWithoutUrlGivesTheFixed400(string environment)
    {
        await using TestApp app = new TestApp(environment);
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/check");

        await AssertIsFixedUnreadableAnswerAsync(HttpStatusCode.BadRequest, response);
    }

    [Theory]
    [InlineData(Development)]
    [InlineData(Production)]
    public async Task HistoryWithTextAsPageNumberGivesTheFixed400(string environment)
    {
        await using TestApp app = new TestApp(environment);
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();

        HttpResponseMessage response = await client.GetAsync($"/services/{Guid.NewGuid()}/history?skip=secret-value");

        await AssertIsFixedUnreadableAnswerAsync(HttpStatusCode.BadRequest, response);
    }

    // Thrown from the fake repository, because no test sends a body large enough to get a real 413.
    // It shows that the status code from the exception is kept, and that its texts are not shown.
    [Theory]
    [InlineData(Development)]
    [InlineData(Production)]
    public async Task BadRequestExceptionKeepsItsStatusCodeButNotItsTexts(string environment)
    {
        await using TestApp app = new TestApp(environment);
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        InvalidOperationException inner = new InvalidOperationException(SecretText);
        app.RepositoryControl.ExceptionFromGetAll = new BadHttpRequestException(SecretText, StatusCodes.Status413PayloadTooLarge, inner);

        HttpResponseMessage response = await client.GetAsync("/services");

        await AssertIsFixedUnreadableAnswerAsync(HttpStatusCode.RequestEntityTooLarge, response);
    }

    // Readable JSON with nothing in it reaches the validation, which gives its own fixed text.
    [Theory]
    [InlineData(Development)]
    [InlineData(Production)]
    public async Task EmptyObjectGivesTheValidationText(string environment)
    {
        await using TestApp app = new TestApp(environment);
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();

        HttpResponseMessage response = await client.PostAsync("/services", CreateJsonContent("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Name must contain 1 to 200 characters.", body.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(Development)]
    [InlineData(Production)]
    public async Task UnexpectedExceptionGivesA500WithoutAnyDetails(string environment)
    {
        await using TestApp app = new TestApp(environment);
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        app.RepositoryControl.ExceptionFromGetAll = new InvalidOperationException(SecretText);

        HttpResponseMessage response = await client.GetAsync("/services");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        AssertNoInternalDetails(json);
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(json);
        string[] expectedFields = { "type", "title", "status", "traceId" };
        Assert.Equal(expectedFields, GetFieldNames(body));
    }

    private static StringContent CreateJsonContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static async Task AssertIsFixedUnreadableAnswerAsync(HttpStatusCode expectedStatus, HttpResponseMessage response)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        AssertNoInternalDetails(json);

        JsonElement body = JsonSerializer.Deserialize<JsonElement>(json);
        string[] expectedFields = { "type", "title", "status", "detail", "traceId" };
        Assert.Equal(expectedFields, GetFieldNames(body));
        Assert.Equal("Invalid request", body.GetProperty("title").GetString());
        Assert.Equal("The request could not be read.", body.GetProperty("detail").GetString());
    }

    // The framework's own message names the parameter and the class, and the inner exception
    // holds the JSON path and position. A stack trace would show classes and file paths.
    private static void AssertNoInternalDetails(string body)
    {
        string[] forbiddenTexts =
        {
            "secret",
            "RegisterServiceRequest",
            "Exception",
            "System.",
            "Microsoft.",
            "DeadSwitch.",
            "LineNumber",
            "BytePosition",
            "   at ",
            ".cs",
            ":\\"
        };

        foreach (string forbidden in forbiddenTexts)
        {
            Assert.DoesNotContain(forbidden, body);
        }
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
