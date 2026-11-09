using System.Net;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Monitoring;

namespace DeadSwitch.Tests;

// The real probe with a stand-in for the network, so nothing leaves the machine.
public class RedirectTargetProbeTests
{
    private const string OwnUrl = "https://monitored.test/";
    private const string LocationHeader = "Location";

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task ARedirectToAnotherHostIsStillUpAndIsSentOnce(HttpStatusCode statusCode)
    {
        ScriptedHttpHandler handler = CreateRedirect(statusCode, "https://other.example/");

        ProbeResult result = await CheckAsync(handler);

        Assert.Equal(ServiceStatus.Up, result.Status);
        Assert.Equal(1, handler.Requests);
        Assert.NotNull(result.RedirectTarget);
        Assert.Equal(RedirectFinding.OtherHost, result.RedirectTarget.Finding);
        Assert.Equal("other.example", result.RedirectTarget.Host);
    }

    [Theory]
    [InlineData(@"/\other.example")]
    [InlineData("///other.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://other example/")]
    [InlineData("https://a@b@other.example/")]
    public async Task AHostileValueIsStillUpAndIsNeverKept(string location)
    {
        ScriptedHttpHandler handler = CreateRedirect(HttpStatusCode.Found, location);

        ProbeResult result = await CheckAsync(handler);

        Assert.Equal(ServiceStatus.Up, result.Status);
        Assert.Equal(1, handler.Requests);
        Assert.NotNull(result.RedirectTarget);
        Assert.Equal(RedirectFinding.Unreadable, result.RedirectTarget.Finding);
        Assert.DoesNotContain("other", result.ToString());
    }

    [Fact]
    public async Task ASuccessIsSavedAsNoRedirect()
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK);

        ProbeResult result = await CheckAsync(handler);

        Assert.NotNull(result.RedirectTarget);
        Assert.Equal(RedirectFinding.NoRedirect, result.RedirectTarget.Finding);
    }

    [Fact]
    public async Task AnErrorHasNoFinding()
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        ProbeResult result = await CheckAsync(handler);

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Null(result.RedirectTarget);
    }

    [Fact]
    public async Task ATimeoutHasNoFinding()
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.NeverAnswers = true;

        ProbeResult result = await CheckAsync(handler);

        Assert.Null(result.RedirectTarget);
    }

    [Fact]
    public async Task ANetworkFailureHasNoFinding()
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.Failure = new HttpRequestException("failure");

        ProbeResult result = await CheckAsync(handler);

        Assert.Null(result.RedirectTarget);
    }

    private static ScriptedHttpHandler CreateRedirect(HttpStatusCode statusCode, string location)
    {
        ScriptedHttpHandler handler = new ScriptedHttpHandler();
        handler.Response = new HttpResponseMessage(statusCode);
        handler.Response.Headers.TryAddWithoutValidation(LocationHeader, location);
        return handler;
    }

    private static async Task<ProbeResult> CheckAsync(ScriptedHttpHandler handler)
    {
        using HttpClient client = new HttpClient(handler);
        HttpHealthProbe probe = new HttpHealthProbe(client, new TlsHandshakeRecorder(TimeProvider.System));
        return await probe.CheckAsync(OwnUrl, 1, CancellationToken.None);
    }
}
