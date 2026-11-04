using System.Net;
using DeadSwitch.Application.Services;

namespace DeadSwitch.Infrastructure.Monitoring;

// Security-critical: this configuration is part of the SSRF protection.
public static class ProtectedHttpHandler
{
    public static SocketsHttpHandler Create()
    {
        PublicInternetConnector connector = new PublicInternetConnector(
            Dns.GetHostAddressesAsync,
            UrlSafety.IsPublicAddress);

        return Create(connector);
    }

    // Internal on purpose: a connector with other rules can allow private addresses. Only the
    // tests may build one, so the application cannot compile a route around the protection.
    internal static SocketsHttpHandler Create(PublicInternetConnector connector)
    {
        return new SocketsHttpHandler
        {
            // A redirect could point to an internal address, so redirects are never followed.
            AllowAutoRedirect = false,
            // A proxy would make the connection itself and bypass the address check below.
            UseProxy = false,
            // A monitor has no use for cookies, and one service's cookies must never reach another.
            UseCookies = false,
            // Replaces the normal connection step, so every address is checked before connecting.
            ConnectCallback = connector.ConnectAsync,
            // A reused connection skips ConnectCallback. This limits how long it is used before the
            // address is checked again.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            // Known limitation: this also applies when a service's own TimeoutSeconds is higher.
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
    }
}
