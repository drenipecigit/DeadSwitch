using System.Net;

namespace DeadSwitch.Tests;

// DNS rebinding: another site points its own name at 127.0.0.1. The browser then treats the API as the
// same origin and hands over every answer. The name is still in the Host header, and that is what
// stops it here.
public class AllowedHostsTests
{
    private const string Development = "Development";
    private const string Production = "Production";

    [Theory]
    [InlineData(Development, "evil.example", "/")]
    [InlineData(Development, "evil.example", "/services/overview")]
    [InlineData(Development, "evil.example:5201", "/services")]
    [InlineData(Development, "localhost.evil.example", "/services")]
    [InlineData(Development, "127.0.0.1.evil.example", "/services")]
    [InlineData(Production, "evil.example", "/")]
    [InlineData(Production, "evil.example", "/services/overview")]
    [InlineData(Production, "evil.example:5201", "/services")]
    [InlineData(Production, "localhost.evil.example", "/services")]
    [InlineData(Production, "127.0.0.1.evil.example", "/services")]
    public async Task AnotherHostNameIsRefused(string environment, string host, string path)
    {
        await using TestApp app = new TestApp(environment);
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        client.DefaultRequestHeaders.Host = host;

        HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(Development, "localhost")]
    [InlineData(Development, "localhost:5201")]
    [InlineData(Development, "127.0.0.1:5201")]
    [InlineData(Development, "[::1]:5201")]
    [InlineData(Production, "localhost")]
    [InlineData(Production, "localhost:5201")]
    [InlineData(Production, "127.0.0.1:5201")]
    [InlineData(Production, "[::1]:5201")]
    public async Task TheNamesOfThisMachineAreAllowed(string environment, string host)
    {
        await using TestApp app = new TestApp(environment);
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        client.DefaultRequestHeaders.Host = host;

        HttpResponseMessage response = await client.GetAsync("/services/overview");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
