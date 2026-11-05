using System.Net;
using DeadSwitch.Api.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeadSwitch.Tests;

// No test here opens a port on the network. The addresses are only text, or something the in-memory
// test server claims. xUnit runs the tests of one class one at a time, so the exit code is not shared.
public class LocalOnlyCheckTests
{
    [Theory]
    [InlineData("http://localhost:5201")]
    [InlineData("https://localhost:7072")]
    [InlineData("http://LOCALHOST:5201")]
    [InlineData("http://127.0.0.1:5201")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://[::1]:5201")]
    public void AnAddressOnThisMachineIsLocal(string address)
    {
        Assert.True(LocalOnlyCheck.IsLocalAddress(address));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5201")]
    [InlineData("http://[::]:5201")]
    [InlineData("http://*:5201")]
    [InlineData("http://+:5201")]
    [InlineData("http://192.168.1.5:5201")]
    [InlineData("http://10.0.0.1:5201")]
    [InlineData("http://172.16.0.1:5201")]
    [InlineData("http://[fe80::1]:5201")]
    [InlineData("http://mymachine:5201")]
    [InlineData("http://localhost.evil.example:5201")]
    [InlineData("http://unix:/tmp/deadswitch.sock")]
    [InlineData("not an address")]
    [InlineData("")]
    public void AnyOtherAddressIsNotLocal(string address)
    {
        Assert.False(LocalOnlyCheck.IsLocalAddress(address));
    }

    [Theory]
    [InlineData("Development", "http://0.0.0.0:5201")]
    [InlineData("Development", "http://[::]:5201")]
    [InlineData("Production", "http://0.0.0.0:5201")]
    public async Task AnAddressOnTheNetworkStopsTheApplication(string environment, string address)
    {
        await using TestApp app = new TestApp(environment);
        app.PretendedAddress = address;

        try
        {
            await WaitUntilStoppingAsync(app);

            Assert.Equal(1, Environment.ExitCode);
            Assert.True(app.Logs.Contains(LogLevel.Critical, address));
        }
        finally
        {
            // The exit code belongs to the whole test process, so it must not stay set.
            Environment.ExitCode = 0;
        }
    }

    [Theory]
    [InlineData("Development", "http://localhost:5201")]
    [InlineData("Development", "http://127.0.0.1:5201")]
    [InlineData("Production", "http://[::1]:5201")]
    public async Task AnAddressOnThisMachineKeepsTheApplicationRunning(string environment, string address)
    {
        await using TestApp app = new TestApp(environment);
        app.PretendedAddress = address;
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health");

        IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.Equal(0, Environment.ExitCode);
        Assert.False(app.Logs.Contains(LogLevel.Critical, address));
    }

    private static async Task WaitUntilStoppingAsync(TestApp app)
    {
        try
        {
            // Reading Services is what starts the application.
            _ = app.Services;
        }
        catch (ObjectDisposedException)
        {
            // The check can stop the application before the test host has finished starting it.
        }

        // The limit is only there so that a bug gives a red test instead of a test that hangs.
        await app.ApplicationStopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
