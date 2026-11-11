using Microsoft.Extensions.Logging;

namespace DeadSwitch.Tests;

// EF Core logs every SQL command on Information. The worker reads the database every second, so the
// log would fill up with these lines. The level is set in appsettings.json, which the tests read too.
// Development reads that file first as well, so the setting holds there without being repeated.
public class DatabaseCommandLoggingTests
{
    // The text EF Core starts every such log line with.
    private const string CommandLogText = "Executed DbCommand";

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task SqlCommandsAreNotLoggedOnInformation(string environment)
    {
        await using TestApp app = new TestApp(environment);
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/services");

        response.EnsureSuccessStatusCode();
        Assert.False(app.Logs.Contains(LogLevel.Information, CommandLogText));
    }
}
