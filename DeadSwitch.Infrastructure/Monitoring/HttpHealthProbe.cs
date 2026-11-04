using System.Diagnostics;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;

namespace DeadSwitch.Infrastructure.Monitoring;

public sealed class HttpHealthProbe(HttpClient client) : IHealthProbe
{
    public async Task<ProbeResult> CheckAsync(string url, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var uri = UrlSafety.Validate(url);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return new(response.IsSuccessStatusCode ? "UP" : "DOWN", (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds, response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new("DOWN", null, stopwatch.ElapsedMilliseconds, "Check timed out.");
        }
        catch (HttpRequestException)
        {
            return new("DOWN", null, stopwatch.ElapsedMilliseconds, "Connection, DNS, TLS or destination policy failure.");
        }
        catch (ArgumentException)
        {
            return new("DOWN", null, stopwatch.ElapsedMilliseconds, "URL rejected by destination policy.");
        }
    }
}
