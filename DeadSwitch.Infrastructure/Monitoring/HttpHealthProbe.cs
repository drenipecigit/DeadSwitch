using System.Diagnostics;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Infrastructure.Monitoring;

public sealed class HttpHealthProbe : IHealthProbe
{
    private readonly HttpClient _client;

    public HttpHealthProbe(HttpClient client)
    {
        _client = client;
    }

    public async Task<ProbeResult> CheckAsync(string url, int timeoutSeconds, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        // Linked: this token is cancelled both by the service's timeout and by the caller.
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            Uri uri = UrlSafety.Validate(url);
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);

            // ResponseHeadersRead: stop waiting at the headers. The status code is all a health
            // check needs, and a large body must not affect the measured time.
            using HttpResponseMessage response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            int statusCode = (int)response.StatusCode;

            // A redirect proves that the server answered, so it counts as UP. The target is never
            // followed or checked, which means a redirect to a broken page also counts as UP.
            bool isRedirect = statusCode >= 300 && statusCode <= 399;

            if (response.IsSuccessStatusCode || isRedirect)
            {
                return new ProbeResult(ServiceStatus.Up, statusCode, stopwatch.ElapsedMilliseconds, null);
            }

            return new ProbeResult(ServiceStatus.Down, statusCode, stopwatch.ElapsedMilliseconds, $"HTTP {statusCode}");
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
            return new ProbeResult(ServiceStatus.Down, null, stopwatch.ElapsedMilliseconds, failureReason);
        }
        catch (ArgumentException)
        {
            // Unknown and not Down: the URL was rejected before any request was sent.
            return new ProbeResult(ServiceStatus.Unknown, null, stopwatch.ElapsedMilliseconds, "URL rejected by destination policy.");
        }
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
