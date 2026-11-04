using System.Net;
using DeadSwitch.Application.Services;
using DeadSwitch.Infrastructure.Monitoring;

namespace DeadSwitch.Tests;

public class ProbeAndPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://10.0.0.1")]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://[::1]")]
    [InlineData("http://[::ffff:127.0.0.1]")]
    [InlineData("http://2130706433")]
    [InlineData("http://localhost")]
    [InlineData("http://test.localhost")]
    [InlineData("file:///C:/Windows")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com:8080")]
    [InlineData("https://example.com/#fragment")]
    public void UnsafeUrlsAreRejected(string url) => Assert.Throws<ArgumentException>(() => UrlSafety.Validate(url));

    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("172.31.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:7f00:1::")]
    public void NonPublicAddressesAreRejected(string ip) => Assert.False(UrlSafety.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void PublicAddressesAreAllowed(string ip) => Assert.True(UrlSafety.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData(200, "UP", null)]
    [InlineData(204, "UP", null)]
    [InlineData(302, "DOWN", "HTTP 302")]
    [InlineData(503, "DOWN", "HTTP 503")]
    public async Task ProbeRecordsHttpOutcome(int code, string status, string? failure)
    {
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code))));
        var result = await new HttpHealthProbe(client).CheckAsync("https://example.com", 1, default);
        Assert.Equal(status, result.Status);
        Assert.Equal(code, result.StatusCode);
        Assert.Equal(failure, result.FailureReason);
        Assert.True(result.ResponseTimeMs >= 0);
    }

    [Fact]
    public async Task NetworkFailureDoesNotEscape()
    {
        using var client = new HttpClient(new StubHandler((_, _) => throw new HttpRequestException("secret destination")));
        var result = await new HttpHealthProbe(client).CheckAsync("https://example.com", 1, default);
        Assert.Equal("DOWN", result.Status);
        Assert.Null(result.StatusCode);
        Assert.DoesNotContain("secret", result.FailureReason!);
    }

    [Fact]
    public async Task TimeoutIsRecordedAsDown()
    {
        using var client = new HttpClient(new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var result = await new HttpHealthProbe(client).CheckAsync("https://example.com", 1, default);
        Assert.Equal("DOWN", result.Status);
        Assert.Equal("Check timed out.", result.FailureReason);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var client = new HttpClient(new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HttpHealthProbe(client).CheckAsync("https://example.com", 10, cancellation.Token));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
