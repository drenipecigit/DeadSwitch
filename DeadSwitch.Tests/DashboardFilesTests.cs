using System.Net;

namespace DeadSwitch.Tests;

public class DashboardFilesTests
{
    private const string Development = "Development";
    private const string Production = "Production";

    [Theory]
    [InlineData(Development, "/", "text/html")]
    [InlineData(Development, "/index.html", "text/html")]
    [InlineData(Development, "/dashboard.css", "text/css")]
    [InlineData(Development, "/dashboard.js", "text/javascript")]
    [InlineData(Production, "/", "text/html")]
    [InlineData(Production, "/index.html", "text/html")]
    [InlineData(Production, "/dashboard.css", "text/css")]
    [InlineData(Production, "/dashboard.js", "text/javascript")]
    public async Task AFileIsServedWithTheRightContentType(string environment, string path, string expectedType)
    {
        await using TestApp app = new TestApp(environment);
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedType, response.Content.Headers.ContentType!.MediaType);
    }

    // Without this, a browser can keep running an old script after the file has changed.
    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/dashboard.css")]
    [InlineData("/dashboard.js")]
    public async Task AFileMustBeCheckedAgainBeforeItIsReused(string path)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path);

        Assert.True(response.Headers.CacheControl!.NoCache);
    }

    [Fact]
    public async Task ThePageLoadsItsOwnScriptAndStyleFiles()
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();

        string page = await client.GetStringAsync("/");

        Assert.Contains("<script src=\"/dashboard.js\"></script>", page);
        Assert.Contains("<link rel=\"stylesheet\" href=\"/dashboard.css\">", page);
    }

    // Only the wwwroot folder is served. The database and the settings sit one level above it.
    [Theory]
    [InlineData("/appsettings.json")]
    [InlineData("/deadswitch.db")]
    [InlineData("/Program.cs")]
    [InlineData("/../appsettings.json")]
    [InlineData("/%2e%2e/appsettings.json")]
    public async Task FilesOutsideTheFolderAreNotServed(string path)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard.js")]
    public async Task TheFilesCannotBeChangedWithAPost(string path)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.PostAsync(path, null);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
