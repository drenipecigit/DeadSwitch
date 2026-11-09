using System.Net;
using DeadSwitch.Application.Services;

namespace DeadSwitch.Infrastructure.Monitoring;

// Security-critical: this configuration is part of the SSRF protection.
public static class ProtectedHttpHandler
{
    public static SocketsHttpHandler Create()
    {
        return Create(CreatePublicInternetConnector());
    }

    // For the health probe: the same handler, which also notes the certificate of each handshake.
    public static SocketsHttpHandler Create(TlsHandshakeRecorder recorder)
    {
        return Create(CreatePublicInternetConnector(), recorder);
    }

    private static PublicInternetConnector CreatePublicInternetConnector()
    {
        return new PublicInternetConnector(Dns.GetHostAddressesAsync, UrlSafety.IsPublicAddress);
    }

    // Security-critical: this callback replaces the framework's own decision about the certificate.
    // OnRemoteCertificate must therefore give the same answer as the framework. No separate connection
    // is opened: the certificate is the one presented on the connection that carries the request.
    internal static SocketsHttpHandler Create(PublicInternetConnector connector, TlsHandshakeRecorder recorder)
    {
        SocketsHttpHandler handler = Create(connector);
        handler.SslOptions.RemoteCertificateValidationCallback = recorder.OnRemoteCertificate;
        return handler;
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
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // In kilobytes. The same as the framework's default, written out because the headers come
            // from a server we do not control, and this limit should be a decision and not a coincidence.
            MaxResponseHeadersLength = 64
        };
    }
}
