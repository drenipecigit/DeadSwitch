using DeadSwitch.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace DeadSwitch.Tests;

// Looks at the handler Program.cs registers for Discord. Only 127.0.0.1 is used, so no request
// leaves the machine even if the protection were missing.
public class DiscordHandlerTests
{
    [Fact]
    public async Task RegisteredHandlerBlocksLoopbackAddress()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        await using TestApp app = new TestApp();
        using HttpClient client = new HttpClient(CreateRegisteredHandler(app), disposeHandler: false);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync($"http://127.0.0.1:{server.Port}/ok"));

        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task RegisteredHandlerHasSafeSettings()
    {
        await using TestApp app = new TestApp();

        // The factory wraps the real handler in its own handlers, so walk down to the innermost one.
        HttpMessageHandler current = CreateRegisteredHandler(app);
        while (current is DelegatingHandler wrapper)
        {
            current = wrapper.InnerHandler!;
        }

        SocketsHttpHandler handler = Assert.IsType<SocketsHttpHandler>(current);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ConnectCallback);
    }

    private static HttpMessageHandler CreateRegisteredHandler(TestApp app)
    {
        IHttpMessageHandlerFactory factory = app.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        // AddHttpClient<INotifier, ...> names the client after the interface.
        return factory.CreateHandler(nameof(INotifier));
    }
}
