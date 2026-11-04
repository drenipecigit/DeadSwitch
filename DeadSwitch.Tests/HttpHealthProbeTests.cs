using System.Net;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Infrastructure.Monitoring;

namespace DeadSwitch.Tests;

public class HttpHealthProbeTests
{
    [Theory]
    [InlineData(200, "UP", null)]
    [InlineData(204, "UP", null)]
    [InlineData(302, "DOWN", "HTTP 302")]
    [InlineData(503, "DOWN", "HTTP 503")]
    public async Task ProbeRecordsHttpOutcome(int code, string status, string? failure)
    {
        StubHandler handler = new StubHandler((request, cancellationToken) =>
        {
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code));
        });
        using HttpClient client = new HttpClient(handler);
        HttpHealthProbe probe = new HttpHealthProbe(client);

        ProbeResult result = await probe.CheckAsync("https://example.com", 1, CancellationToken.None);

        Assert.Equal(status, result.Status);
        Assert.Equal(code, result.StatusCode);
        Assert.Equal(failure, result.FailureReason);
        Assert.True(result.ResponseTimeMs >= 0);
    }

    [Fact]
    public async Task NetworkFailureDoesNotEscape()
    {
        StubHandler handler = new StubHandler((request, cancellationToken) =>
        {
            throw new HttpRequestException("secret destination");
        });
        using HttpClient client = new HttpClient(handler);
        HttpHealthProbe probe = new HttpHealthProbe(client);

        ProbeResult result = await probe.CheckAsync("https://example.com", 1, CancellationToken.None);

        Assert.Equal("DOWN", result.Status);
        Assert.Null(result.StatusCode);
        Assert.DoesNotContain("secret", result.FailureReason!);
    }

    [Fact]
    public async Task TimeoutIsRecordedAsDown()
    {
        using HttpClient client = new HttpClient(new StubHandler(HangUntilCancelledAsync));
        HttpHealthProbe probe = new HttpHealthProbe(client);

        ProbeResult result = await probe.CheckAsync("https://example.com", 1, CancellationToken.None);

        Assert.Equal("DOWN", result.Status);
        Assert.Equal("Check timed out.", result.FailureReason);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using HttpClient client = new HttpClient(new StubHandler(HangUntilCancelledAsync));
        HttpHealthProbe probe = new HttpHealthProbe(client);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.CheckAsync("https://example.com", 10, cancellation.Token));
    }

    private static async Task<HttpResponseMessage> HangUntilCancelledAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    // Replaces the network: the probe's HttpClient calls this instead of sending a real request.
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return send(request, cancellationToken);
        }
    }
}
