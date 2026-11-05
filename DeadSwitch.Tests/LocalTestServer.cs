using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DeadSwitch.Tests;

// A real web server on 127.0.0.1 that remembers every request it receives. A blocked request
// can then be proven by the server never having seen it, not only by an exception being thrown.
internal sealed class LocalTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<ReceivedRequest> _requests = new ConcurrentQueue<ReceivedRequest>();

    public int Port { get; private set; }

    private LocalTestServer(WebApplication app)
    {
        _app = app;
    }

    public static async Task<LocalTestServer> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        // The API's appsettings.json is copied to the test folder and would be read here as well.
        // This server plays a monitored website, so it has to answer to any name a test gives it.
        builder.Configuration["AllowedHosts"] = "*";

        // Port 0 lets the operating system pick a free port, so tests never collide.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        LocalTestServer server = new LocalTestServer(builder.Build());
        server.MapRoutes();
        await server._app.StartAsync();

        server.Port = new Uri(server._app.Urls.First()).Port;
        return server;
    }

    public int RequestCount
    {
        get { return _requests.Count; }
    }

    public IReadOnlyList<ReceivedRequest> Requests
    {
        get { return _requests.ToArray(); }
    }

    public int CountRequestsTo(string path)
    {
        int count = 0;
        foreach (ReceivedRequest request in _requests)
        {
            if (request.Path == path)
            {
                count++;
            }
        }

        return count;
    }

    private void MapRoutes()
    {
        _app.Use(async (context, next) =>
        {
            string path = context.Request.Path.ToString();
            string host = context.Request.Host.ToString();
            string? cookie = context.Request.Headers.Cookie.FirstOrDefault();
            _requests.Enqueue(new ReceivedRequest(path, host, cookie));

            await next(context);
        });

        _app.MapGet("/ok", () => Results.Ok());
        _app.MapGet("/secret", () => Results.Ok());
        _app.MapGet("/redirect", () => Results.Redirect("/secret"));
        _app.MapGet("/set-cookie", (HttpContext context) =>
        {
            context.Response.Headers.SetCookie = "session=abc";
            return Results.Ok();
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
    }
}

internal sealed record ReceivedRequest(string Path, string Host, string? Cookie);
