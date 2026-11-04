using System.Net;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Monitoring;

namespace DeadSwitch.Tests;

public class HttpHealthProbeTests
{
    [Theory]
    [InlineData(200, ServiceStatus.Up, null)]
    [InlineData(204, ServiceStatus.Up, null)]
    [InlineData(299, ServiceStatus.Up, null)]
    [InlineData(300, ServiceStatus.Up, null)]
    [InlineData(301, ServiceStatus.Up, null)]
    [InlineData(302, ServiceStatus.Up, null)]
    [InlineData(304, ServiceStatus.Up, null)]
    [InlineData(399, ServiceStatus.Up, null)]
    [InlineData(400, ServiceStatus.Down, "HTTP 400")]
    [InlineData(503, ServiceStatus.Down, "HTTP 503")]
    public async Task ProbeRecordsHttpOutcome(int code, ServiceStatus status, string? failure)
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

    // The stub always answers with a redirect, so a probe that followed it would call the stub again.
    // The address is loopback on purpose: this test must never point at a real target.
    [Fact]
    public async Task RedirectIsRecordedAsUpWithoutSendingASecondRequest()
    {
        int requests = 0;
        StubHandler handler = new StubHandler((request, cancellationToken) =>
        {
            requests++;
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://127.0.0.1/secret");
            return Task.FromResult(response);
        });
        using HttpClient client = new HttpClient(handler);
        HttpHealthProbe probe = new HttpHealthProbe(client);

        ProbeResult result = await probe.CheckAsync("https://example.com", 1, CancellationToken.None);

        Assert.Equal(ServiceStatus.Up, result.Status);
        Assert.Equal(302, result.StatusCode);
        Assert.Null(result.FailureReason);
        Assert.Equal(1, requests);
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

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Null(result.StatusCode);
        Assert.DoesNotContain("secret", result.FailureReason!);
    }

    [Fact]
    public async Task TimeoutIsRecordedAsDown()
    {
        using HttpClient client = new HttpClient(new StubHandler(HangUntilCancelledAsync));
        HttpHealthProbe probe = new HttpHealthProbe(client);

        ProbeResult result = await probe.CheckAsync("https://example.com", 1, CancellationToken.None);

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Equal("Check timed out.", result.FailureReason);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("ftp://example.com/")]
    public async Task RejectedUrlIsRecordedAsUnknownWithoutSendingARequest(string url)
    {
        int requests = 0;
        StubHandler handler = new StubHandler((request, cancellationToken) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using HttpClient client = new HttpClient(handler);
        HttpHealthProbe probe = new HttpHealthProbe(client);

        ProbeResult result = await probe.CheckAsync(url, 1, CancellationToken.None);

        Assert.Equal(ServiceStatus.Unknown, result.Status);
        Assert.Null(result.StatusCode);
        Assert.Equal("URL rejected by destination policy.", result.FailureReason);
        Assert.Equal(0, requests);
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
