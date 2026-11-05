using System.Net;

namespace DeadSwitch.Tests;

// The expected values are written out here on purpose, so a weakened header in the code fails a test.
public class DashboardSecurityHeadersTests
{
    private const string Development = "Development";
    private const string Production = "Production";

    private const string ExpectedPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    [Theory]
    [InlineData(Development, "/", HttpStatusCode.OK)]
    [InlineData(Development, "/index.html", HttpStatusCode.OK)]
    [InlineData(Development, "/dashboard.js", HttpStatusCode.OK)]
    [InlineData(Development, "/dashboard.css", HttpStatusCode.OK)]
    [InlineData(Development, "/services/overview", HttpStatusCode.OK)]
    [InlineData(Development, "/no-such-path", HttpStatusCode.NotFound)]
    [InlineData(Development, "/check", HttpStatusCode.BadRequest)]
    [InlineData(Production, "/", HttpStatusCode.OK)]
    [InlineData(Production, "/index.html", HttpStatusCode.OK)]
    [InlineData(Production, "/dashboard.js", HttpStatusCode.OK)]
    [InlineData(Production, "/dashboard.css", HttpStatusCode.OK)]
    [InlineData(Production, "/services/overview", HttpStatusCode.OK)]
    [InlineData(Production, "/no-such-path", HttpStatusCode.NotFound)]
    [InlineData(Production, "/check", HttpStatusCode.BadRequest)]
    public async Task EveryAnswerHasAllSecurityHeaders(string environment, string path, HttpStatusCode expectedStatus)
    {
        await using TestApp app = new TestApp(environment);
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(expectedStatus, response.StatusCode);
        AssertHasAllSecurityHeaders(response);
    }

    // The exception handler empties the headers before it writes its answer. This test fails if the
    // headers are set when the request starts and not just before the answer is sent.
    [Theory]
    [InlineData(Development)]
    [InlineData(Production)]
    public async Task AnUnexpectedErrorAlsoHasAllSecurityHeaders(string environment)
    {
        await using TestApp app = new TestApp(environment);
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        app.RepositoryControl.FailGetAll = true;

        HttpResponseMessage response = await client.GetAsync("/services");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertHasAllSecurityHeaders(response);
    }

    [Fact]
    public async Task ThePolicyAllowsNoInlineCodeAndNoEval()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/");
        string policy = GetOnlyValue(response, "Content-Security-Policy");

        Assert.DoesNotContain("unsafe-inline", policy);
        Assert.DoesNotContain("unsafe-eval", policy);
        Assert.DoesNotContain("*", policy);
        Assert.DoesNotContain("http:", policy);
        Assert.DoesNotContain("https:", policy);
        Assert.DoesNotContain("data:", policy);
    }

    private static void AssertHasAllSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal(ExpectedPolicy, GetOnlyValue(response, "Content-Security-Policy"));
        Assert.Equal("nosniff", GetOnlyValue(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", GetOnlyValue(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", GetOnlyValue(response, "Referrer-Policy"));
    }

    private static string GetOnlyValue(HttpResponseMessage response, string headerName)
    {
        IEnumerable<string>? values;
        bool found = response.Headers.TryGetValues(headerName, out values);
        Assert.True(found, $"The answer has no {headerName} header.");
        return Assert.Single(values!);
    }
}
