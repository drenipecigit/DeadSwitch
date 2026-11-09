using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Infrastructure.Monitoring;

public sealed class HttpHealthProbe : IHealthProbe
{
    internal const string CertificateNotValidReason = CertificateReasons.NotValid;
    internal const string NoNewHandshakeReason = CertificateReasons.NoNewHandshake;

    private readonly HttpClient _client;
    private readonly TlsHandshakeRecorder _recorder;

    public HttpHealthProbe(HttpClient client, TlsHandshakeRecorder recorder)
    {
        _client = client;
        _recorder = recorder;
    }

    public async Task<ProbeResult> CheckAsync(string url, int timeoutSeconds, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        // Linked: this token is cancelled both by the service's timeout and by the caller.
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        // Holds the host while the recorder can have an entry that this check has not taken yet.
        string? hostWithEntry = null;

        try
        {
            Uri uri = UrlSafety.Validate(url);

            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                hostWithEntry = uri.IdnHost;
            }

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);

            // A reused connection makes no handshake, so there would be no certificate to read. Closing
            // it also means that the address is checked again on every check. Connection: close only
            // exists up to HTTP/1.1, so the version is stated and may not be raised by the server.
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Headers.ConnectionClose = true;

            // ResponseHeadersRead: stop waiting at the headers. The status code and the security headers are
            // all a check needs, and a large body must not affect the measured time.
            using HttpResponseMessage response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);

            int statusCode = (int)response.StatusCode;

            // Read from the response that is already here, so no extra request is sent.
            SecurityHeaderCheck securityHeaders = SecurityHeaderReader.Read(uri, statusCode, response.Headers);

            CertificateCheck certificate = ReadCertificateOfResponse(uri);
            hostWithEntry = null;

            // A redirect proves that the server answered, so it counts as UP. The target is never
            // followed or checked, which means a redirect to a broken page also counts as UP.
            bool isRedirect = statusCode >= 300 && statusCode <= 399;

            if (response.IsSuccessStatusCode || isRedirect)
            {
                return new ProbeResult(ServiceStatus.Up, statusCode, stopwatch.ElapsedMilliseconds, null, securityHeaders, certificate);
            }

            return new ProbeResult(ServiceStatus.Down, statusCode, stopwatch.ElapsedMilliseconds, $"HTTP {statusCode}", securityHeaders, certificate);
        }
        catch (OperationCanceledException)
        {
            // Both a timeout and a cancelled caller end up here, and only the caller's own token
            // can tell them apart. A cancelled caller must not be saved as "the service is down".
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProbeResult(ServiceStatus.Down, null, stopwatch.ElapsedMilliseconds, "Check timed out.");
        }
        catch (HttpRequestException exception)
        {
            // Unknown and not Down: we refused to connect, so we learned nothing about the service.
            if (WasBlocked(exception))
            {
                string blockedReason = "Destination address blocked by policy.";
                return new ProbeResult(ServiceStatus.Unknown, null, stopwatch.ElapsedMilliseconds, blockedReason);
            }

            // A fixed message, because the exception text could reveal details about internal hosts.
            string failureReason = "Connection, DNS or TLS failure.";
            CertificateCheck? certificate = ReadCertificateOfFailure(hostWithEntry, exception);
            hostWithEntry = null;

            return new ProbeResult(ServiceStatus.Down, null, stopwatch.ElapsedMilliseconds, failureReason, null, certificate);
        }
        catch (ServiceValidationException)
        {
            // Unknown and not Down: the URL was rejected before any request was sent.
            return new ProbeResult(ServiceStatus.Unknown, null, stopwatch.ElapsedMilliseconds, "URL rejected by destination policy.");
        }
        finally
        {
            // In finally, so that a timeout or any other way out also removes the entry. A later check of
            // the same host would otherwise be able to read it.
            if (hostWithEntry != null)
            {
                _recorder.Take(hostWithEntry);
            }
        }
    }

    // A response was received, so the certificate was accepted on this connection. Whatever the entry
    // says, this can only give a date or say that nothing was seen, and never that it was invalid.
    private CertificateCheck ReadCertificateOfResponse(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return new CertificateCheck(null, SecurityHeaderReader.NotHttpsReason);
        }

        TlsHandshake? handshake = _recorder.Take(uri.IdnHost);

        if (handshake == null || handshake.NotAfterUtc == null)
        {
            return new CertificateCheck(null, NoNewHandshakeReason);
        }

        return new CertificateCheck(handshake.NotAfterUtc, null);
    }

    // Security-critical: both conditions are needed. An entry can come from another check of the same
    // host, so it says nothing about this check's own failure unless that failure was a TLS failure.
    private CertificateCheck? ReadCertificateOfFailure(string? host, HttpRequestException exception)
    {
        if (host == null)
        {
            return null;
        }

        TlsHandshake? handshake = _recorder.Take(host);

        if (handshake == null || !handshake.IsRejected)
        {
            return null;
        }

        if (!WasTlsFailure(exception))
        {
            return null;
        }

        return new CertificateCheck(null, CertificateNotValidReason);
    }

    private static bool WasTlsFailure(HttpRequestException exception)
    {
        Exception? current = exception;

        while (current != null)
        {
            if (current is AuthenticationException)
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    // HttpClient wraps an exception from the connection step in its own HttpRequestException, so the
    // blocked exception has to be looked for further down. A miss here only makes the status DOWN.
    // The connection has already been stopped by then, so this code is not part of the protection.
    private static bool WasBlocked(HttpRequestException exception)
    {
        Exception? current = exception;

        while (current != null)
        {
            if (current is DestinationBlockedException)
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }
}
