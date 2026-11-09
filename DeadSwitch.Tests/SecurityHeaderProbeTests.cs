using System.Net;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Monitoring;

namespace DeadSwitch.Tests;

// The real probe with a stand-in for the network. Nothing leaves the machine.
public class SecurityHeaderProbeTests
{
    private const string HttpsUrl = "https://example.com";
    private const string HttpUrl = "http://example.com";
    private const string LocationMarker = "location-marker";

    [Fact]
    public async Task ASuccessOverHttpsGetsAGradeFromTheSameRequest()
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK);
        TestSecurityHeaders.AddAllExcept(handler.Response.Headers, TestSecurityHeaders.FrameOptions);

        ProbeResult result = await CheckAsync(handler, HttpsUrl);

        Assert.Equal(ServiceStatus.Up, result.Status);
        Assert.Equal(1, handler.Requests);
        Assert.NotNull(result.SecurityHeaders);
        Assert.Equal("B", result.SecurityHeaders.Grade);
        Assert.Equal(TestSecurityHeaders.FrameOptions, result.SecurityHeaders.MissingHeaders);
    }

    [Fact]
    public async Task AnHttpAddressGivesAReasonAndNoGrade()
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK);

        ProbeResult result = await CheckAsync(handler, HttpUrl);

        Assert.NotNull(result.SecurityHeaders);
        Assert.Null(result.SecurityHeaders.Grade);
        Assert.Equal(SecurityHeaderReader.NotHttpsReason, result.SecurityHeaders.NotAssessedReason);
    }

    // The address is loopback on purpose: this test must never point at a real target.
    [Fact]
    public async Task ARedirectIsNotFollowedAndItsTargetIsNotKept()
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.Response = new HttpResponseMessage(HttpStatusCode.Found);
        handler.Response.Headers.Location = new Uri($"http://127.0.0.1/{LocationMarker}");

        ProbeResult result = await CheckAsync(handler, HttpsUrl);

        Assert.Equal(ServiceStatus.Up, result.Status);
        Assert.Equal(1, handler.Requests);
        Assert.NotNull(result.SecurityHeaders);
        Assert.Null(result.SecurityHeaders.Grade);
        Assert.Equal(SecurityHeaderReader.RedirectReason, result.SecurityHeaders.NotAssessedReason);
        Assert.DoesNotContain(LocationMarker, result.ToString());
    }

    [Fact]
    public async Task AnErrorResponseIsDownAndNotAssessed()
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        ProbeResult result = await CheckAsync(handler, HttpsUrl);

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Equal("HTTP 500", result.FailureReason);
        Assert.NotNull(result.SecurityHeaders);
        Assert.Equal(SecurityHeaderReader.NotSuccessReason, result.SecurityHeaders.NotAssessedReason);
    }

    [Fact]
    public async Task ATimeoutHasNothingToAssess()
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.NeverAnswers = true;

        ProbeResult result = await CheckAsync(handler, HttpsUrl);

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Null(result.SecurityHeaders);
    }

    [Fact]
    public async Task ANetworkFailureHasNothingToAssess()
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.Failure = new HttpRequestException("failure");

        ProbeResult result = await CheckAsync(handler, HttpsUrl);

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Null(result.SecurityHeaders);
    }

    [Fact]
    public async Task ABlockedAddressHasNothingToAssess()
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.Failure = new HttpRequestException("blocked", new DestinationBlockedException());

        ProbeResult result = await CheckAsync(handler, HttpsUrl);

        Assert.Equal(ServiceStatus.Unknown, result.Status);
        Assert.Null(result.SecurityHeaders);
    }

    [Fact]
    public async Task ARejectedUrlHasNothingToAssessAndSendsNothing()
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();

        ProbeResult result = await CheckAsync(handler, "http://127.0.0.1/");

        Assert.Equal(ServiceStatus.Unknown, result.Status);
        Assert.Equal(0, handler.Requests);
        Assert.Null(result.SecurityHeaders);
    }

    private static async Task<ProbeResult> CheckAsync(ScriptedHttpHandler handler, string url)
    {
        using HttpClient client = new HttpClient(handler);
        HttpHealthProbe probe = new HttpHealthProbe(client);
        return await probe.CheckAsync(url, 1, CancellationToken.None);
    }
}
