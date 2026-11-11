using System.Net;
using System.Text;
using System.Text.Json;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

// The API has no login yet, so being on this machine is the only permission. A page from another site
// in the owner's browser is also on this machine, which is what these tests guard against.
public class CrossSiteRequestTests
{
    private const string FetchSiteHeader = "Sec-Fetch-Site";
    private const string OriginHeader = "Origin";

    // The address TestApp's client calls.
    private const string OwnOrigin = "http://localhost";

    // {id} is replaced with a real service, so that a request that gets through really does its work.
    public static TheoryData<string, string> AllApiRoutes()
    {
        TheoryData<string, string> routes = new TheoryData<string, string>();
        routes.Add("POST", "/services/{id}/check");
        routes.Add("GET", "/check?url=https://example.com");
        routes.Add("GET", "/services");
        routes.Add("GET", "/services/overview");
        routes.Add("GET", "/services/{id}");
        routes.Add("GET", "/services/{id}/history");
        routes.Add("DELETE", "/services/{id}");
        routes.Add("GET", "/health");
        return routes;
    }

    [Theory]
    [MemberData(nameof(AllApiRoutes))]
    public async Task ARequestFromAnotherSiteIsBlocked(string method, string route)
    {
        await AssertBlockedAsync(method, route, FetchSiteHeader, "cross-site");
    }

    // localhost:3000 counts as the same site as localhost:5201, because the port is not part of a site.
    [Theory]
    [MemberData(nameof(AllApiRoutes))]
    public async Task ARequestFromTheSameSiteOnAnotherPortIsBlocked(string method, string route)
    {
        await AssertBlockedAsync(method, route, FetchSiteHeader, "same-site");
    }

    [Theory]
    [MemberData(nameof(AllApiRoutes))]
    public async Task ARequestWithAnotherOriginIsBlocked(string method, string route)
    {
        await AssertBlockedAsync(method, route, OriginHeader, "https://evil.example");
    }

    // Each of these is close to the real origin, or is what a browser sends from a local file.
    [Theory]
    [InlineData("null")]
    [InlineData("https://localhost")]
    [InlineData("http://localhost:3000")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("http://evil.example/http://localhost")]
    public async Task AnOriginThatIsNotExactlyOursIsBlocked(string origin)
    {
        await AssertBlockedAsync("POST", "/services/{id}/check", OriginHeader, origin);
    }

    // Unknown values are refused, so that a new value in a future browser is not let through by accident.
    [Theory]
    [InlineData("something-new")]
    [InlineData("same-origin, cross-site")]
    public async Task AnUnknownFetchSiteValueIsBlocked(string value)
    {
        await AssertBlockedAsync("POST", "/services/{id}/check", FetchSiteHeader, value);
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    public async Task TheDashboardAndAnAddressTypedByTheOwnerAreAllowed(string value)
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        HttpResponseMessage response = await SendAsync(client, "POST", $"/services/{service.Id}/check", FetchSiteHeader, value);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, app.Probe.Calls);
    }

    [Fact]
    public async Task OurOwnOriginIsAllowed()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        HttpResponseMessage response = await SendAsync(client, "POST", $"/services/{service.Id}/check", OriginHeader, OwnOrigin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, app.Probe.Calls);
    }

    // PowerShell and other programs send neither header. This is not a hole in the CSRF protection,
    // because it is a browser that can be tricked, but it also means this is no protection against
    // another program on the same machine. That has to wait for the login in phase 6.
    [Fact]
    public async Task AProgramThatSendsNeitherHeaderIsAllowed()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);

        HttpResponseMessage response = await client.PostAsync($"/services/{service.Id}/check", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, app.Probe.Calls);
    }

    // The files hold no data and change nothing, and a link to the dashboard from elsewhere must work.
    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/dashboard.js")]
    [InlineData("/dashboard.css")]
    public async Task TheDashboardFilesCanBeOpenedFromAnotherSite(string path)
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await SendAsync(client, "GET", path, FetchSiteHeader, "cross-site");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ABlockedRequestGetsAFixedAnswerAndTheSecurityHeaders(string environment)
    {
        await using TestApp app = new TestApp(environment);
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await SendAsync(client, "GET", "/services", FetchSiteHeader, "cross-site");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.Contains("Content-Security-Policy"));

        string text = await response.Content.ReadAsStringAsync();
        JsonElement body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("Request blocked", body.GetProperty("title").GetString());
        Assert.Equal("Requests from other sites are not allowed.", body.GetProperty("detail").GetString());
    }

    // A form can only send text/plain, form data or multipart. JSON from another site needs permission
    // from the server first, which it never gets. This holds even without the blocking above.
    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data")]
    public async Task ABodyThatAFormCanSendCannotCreateAService(string contentType)
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        string json = "{\"name\":\"Evil\",\"url\":\"https://example.com\"}";
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/services");
        request.Content = new StringContent(json, Encoding.UTF8, contentType);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        string services = await client.GetStringAsync("/services");
        Assert.Equal("[]", services);
    }

    // Without this header a browser refuses to hand the answer to another site, and refuses to send
    // a PUT, a DELETE or JSON at all. The application must therefore never start sending it.
    [Theory]
    [InlineData("OPTIONS", "/services")]
    [InlineData("OPTIONS", "/services/overview")]
    [InlineData("GET", "/services")]
    [InlineData("GET", "/services/overview")]
    [InlineData("GET", "/")]
    public async Task NoAnswerAllowsAnotherSiteToReadIt(string method, string path)
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        HttpRequestMessage request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(OriginHeader, "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "DELETE");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
        Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task AssertBlockedAsync(string method, string route, string headerName, string headerValue)
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await RegisterAsync(app, client);
        string path = route.Replace("{id}", service.Id.ToString());

        HttpResponseMessage response = await SendAsync(client, method, path, headerName, headerValue);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, app.Probe.Calls);

        // Read without the header, to show that the blocked request changed nothing.
        string services = await client.GetStringAsync("/services");
        Assert.Contains(service.Id.ToString(), services);
        string history = await client.GetStringAsync($"/services/{service.Id}/history");
        Assert.Equal("[]", history);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, string headerName, string headerValue)
    {
        HttpRequestMessage request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(headerName, headerValue);
        return await client.SendAsync(request);
    }

    private static async Task<TestApp> CreateAppAsync()
    {
        TestApp app = new TestApp();
        await app.MigrateAsync();
        return app;
    }

    private static Task<MonitoredService> RegisterAsync(TestApp app, HttpClient client)
    {
        return app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", "https://example.com"));
    }
}
