using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace DeadSwitch.Tests;

// A real web server on 127.0.0.1 that remembers every request it receives. A blocked request
// can then be proven by the server never having seen it, not only by an exception being thrown.
internal sealed class LocalTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<ReceivedRequest> _requests = new ConcurrentQueue<ReceivedRequest>();
    private readonly TaskCompletionSource _bothHeldRequestsArrived = new TaskCompletionSource();
    private int _heldRequests;

    public int Port { get; private set; }

    private LocalTestServer(WebApplication app)
    {
        _app = app;
    }

    public static async Task<LocalTestServer> StartAsync()
    {
        WebApplicationBuilder builder = CreateBuilder();

        // Port 0 lets the operating system pick a free port, so tests never collide.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        return await StartAsync(builder);
    }

    // HTTP/2 is offered on purpose: only then would a client that asks for a higher version get one.
    public static async Task<LocalTestServer> StartHttpsAsync(X509Certificate2 certificate)
    {
        WebApplicationBuilder builder = CreateBuilder();
        builder.WebHost.UseUrls("https://127.0.0.1:0");

        void UseCertificate(KestrelServerOptions kestrel)
        {
            kestrel.ConfigureEndpointDefaults(OfferHttp1AndHttp2);
            kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate);
        }

        builder.WebHost.ConfigureKestrel(UseCertificate);

        return await StartAsync(builder);
    }

    private static void OfferHttp1AndHttp2(ListenOptions listen)
    {
        listen.Protocols = HttpProtocols.Http1AndHttp2;
    }

    private static WebApplicationBuilder CreateBuilder()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        // The API's appsettings.json is copied to the test folder and would be read here as well.
        // This server plays a monitored website, so it has to answer to any name a test gives it.
        builder.Configuration["AllowedHosts"] = "*";

        return builder;
    }

    private static async Task<LocalTestServer> StartAsync(WebApplicationBuilder builder)
    {
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
            string protocol = context.Request.Protocol;
            string? connectionHeader = context.Request.Headers.Connection.FirstOrDefault();
            string connectionId = context.Connection.Id;
            _requests.Enqueue(new ReceivedRequest(path, host, cookie, protocol, connectionHeader, connectionId));

            await next(context);
        });

        _app.MapGet("/ok", () => Results.Ok());
        _app.MapGet("/held-first", WaitForBothHeldRequestsAsync);
        _app.MapGet("/held-second", WaitForBothHeldRequestsAsync);
        _app.MapGet("/secret", () => Results.Ok());
        _app.MapGet("/redirect", () => Results.Redirect("/secret"));
        _app.MapGet("/redirect-to-other-name", SendRedirectToOtherName);
        _app.MapGet("/set-cookie", (HttpContext context) =>
        {
            context.Response.Headers.SetCookie = "session=abc";
            return Results.Ok();
        });
        _app.MapGet("/security-headers", SendSecurityHeaders);
        _app.MapGet("/blank-security-header", SendOneBlankSecurityHeader);
        _app.MapGet("/large-header", SendLargeHeader);
    }

    // Neither of the two held requests is answered until both have arrived, so a test knows that the
    // two checks really were running at the same time.
    private async Task<IResult> WaitForBothHeldRequestsAsync()
    {
        int arrived = Interlocked.Increment(ref _heldRequests);
        if (arrived == 2)
        {
            _bothHeldRequestsArrived.TrySetResult();
        }

        await _bothHeldRequestsArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return Results.Ok();
    }

    // Uses this server's own port, so that a client that wrongly followed it would be seen here.
    private static IResult SendRedirectToOtherName(HttpContext context)
    {
        return Results.Redirect($"http://other-name.test:{context.Connection.LocalPort}/secret");
    }

    private static IResult SendSecurityHeaders(HttpContext context)
    {
        foreach (string name in TestSecurityHeaders.AllNames)
        {
            context.Response.Headers[name] = TestSecurityHeaders.OrdinaryValue;
        }

        return Results.Ok();
    }

    // All five are sent, and the one named in the query holds nothing but spaces.
    private static IResult SendOneBlankSecurityHeader(HttpContext context, string name)
    {
        foreach (string headerName in TestSecurityHeaders.AllNames)
        {
            context.Response.Headers[headerName] = TestSecurityHeaders.OrdinaryValue;
        }

        context.Response.Headers[name] = "   ";
        return Results.Ok();
    }

    private static IResult SendLargeHeader(HttpContext context, int kilobytes)
    {
        context.Response.Headers["X-Padding"] = new string('a', kilobytes * 1024);
        return Results.Ok();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
    }
}

internal sealed record ReceivedRequest(string Path, string Host, string? Cookie, string Protocol, string? ConnectionHeader, string ConnectionId);
