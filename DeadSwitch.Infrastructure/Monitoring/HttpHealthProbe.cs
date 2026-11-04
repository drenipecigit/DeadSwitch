using System.Diagnostics;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;

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
            if (response.IsSuccessStatusCode)
            {
                return new ProbeResult("UP", statusCode, stopwatch.ElapsedMilliseconds, null);
            }

            return new ProbeResult("DOWN", statusCode, stopwatch.ElapsedMilliseconds, $"HTTP {statusCode}");
        }
        catch (OperationCanceledException)
        {
            // Both a timeout and a cancelled caller end up here, and only the caller's own token
            // can tell them apart. A cancelled caller must not be saved as "the service is down".
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProbeResult("DOWN", null, stopwatch.ElapsedMilliseconds, "Check timed out.");
        }
        catch (HttpRequestException)
        {
            // A fixed message, because the exception text could reveal details about internal hosts.
            string failureReason = "Connection, DNS, TLS or destination policy failure.";
            return new ProbeResult("DOWN", null, stopwatch.ElapsedMilliseconds, failureReason);
        }
        catch (ArgumentException)
        {
            return new ProbeResult("DOWN", null, stopwatch.ElapsedMilliseconds, "URL rejected by destination policy.");
        }
    }
}
