using System.Net;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Infrastructure.Monitoring;
using Microsoft.Extensions.DependencyInjection;

namespace DeadSwitch.Tests;

// Runs the real connection code against a real local server, and contains no calls to the Internet.
//
// The tests come in two groups:
// - "Registered" tests use the handler exactly as Program.cs registers it, with the real DNS lookup
//   and the real address rule. They prove the protection is actually connected in the application.
// - The other tests control DNS and the address rule, because the server lives on 127.0.0.1 and the
//   real rule would stop everything. They prove how the connection code behaves.
public class SsrfProtectionTests
{
    private static readonly IPAddress AllowedAddress = IPAddress.Parse("127.0.0.1");
    private static readonly IPAddress ForbiddenAddress = IPAddress.Parse("127.0.0.2");

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task RegisteredHandlerBlocksLocalServer(string host)
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        await using TestApp app = new TestApp();
        using HttpClient client = new HttpClient(CreateRegisteredHandler(app), disposeHandler: false);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync($"http://{host}:{server.Port}/ok"));

        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task RegisteredHandlerHasSafeSettings()
    {
        await using TestApp app = new TestApp();

        // The factory wraps the real handler in its own handlers, so walk down to the innermost one.
        HttpMessageHandler current = CreateRegisteredHandler(app);
        while (current is DelegatingHandler wrapper)
        {
            current = wrapper.InnerHandler!;
        }

        SocketsHttpHandler handler = Assert.IsType<SocketsHttpHandler>(current);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ConnectCallback);
        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionLifetime);
    }

    // The control for all the blocking tests: it shows the server can be reached when the rule
    // allows it, so "0 requests" in the other tests really means blocked and not "server unreachable".
    [Fact]
    public async Task AllowedAddressReachesServerAndKeepsHostName()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        FakeDns dns = new FakeDns(new[] { AllowedAddress });
        using HttpClient client = CreateClient(dns, IsAllowedAddress);

        HttpResponseMessage response = await client.GetAsync($"http://monitored.test:{server.Port}/ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"monitored.test:{server.Port}", server.Requests[0].Host);
    }

    [Fact]
    public async Task RedirectIsReturnedAndNeverFollowed()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        FakeDns dns = new FakeDns(new[] { AllowedAddress });
        using HttpClient client = CreateClient(dns, IsAllowedAddress);

        HttpResponseMessage response = await client.GetAsync($"http://monitored.test:{server.Port}/redirect");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, server.CountRequestsTo("/redirect"));
        Assert.Equal(0, server.CountRequestsTo("/secret"));
    }

    [Fact]
    public async Task CookiesAreNotSentBack()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        FakeDns dns = new FakeDns(new[] { AllowedAddress });
        using HttpClient client = CreateClient(dns, IsAllowedAddress);

        await client.GetAsync($"http://monitored.test:{server.Port}/set-cookie");
        await client.GetAsync($"http://monitored.test:{server.Port}/ok");

        Assert.Equal(2, server.RequestCount);
        Assert.Null(server.Requests[1].Cookie);
    }

    [Fact]
    public async Task HostNamePointingToPrivateAddressIsBlockedByRealRule()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        FakeDns dns = new FakeDns(new[] { AllowedAddress });
        using HttpClient client = CreateClient(dns, UrlSafety.IsPublicAddress);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync($"http://looks-public.test:{server.Port}/ok"));

        Assert.Equal(1, dns.Calls);
        Assert.Equal(0, server.RequestCount);
    }

    // The reachable address comes first, so code that tried addresses before checking all of them
    // would reach the server and fail this test.
    [Fact]
    public async Task OneForbiddenAddressAmongSeveralBlocksTheHost()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        FakeDns dns = new FakeDns(new[] { AllowedAddress, ForbiddenAddress });
        using HttpClient client = CreateClient(dns, IsAllowedAddress);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync($"http://mixed.test:{server.Port}/ok"));

        // Without this line the test would also pass if the connector was never used.
        Assert.Equal(1, dns.Calls);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task EmptyDnsAnswerIsBlocked()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        FakeDns dns = new FakeDns(Array.Empty<IPAddress>());
        using HttpClient client = CreateClient(dns, IsAllowedAddress);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync($"http://empty.test:{server.Port}/ok"));

        // Without this line the test would also pass if the connector was never used.
        Assert.Equal(1, dns.Calls);
        Assert.Equal(0, server.RequestCount);
    }

    // DNS rebinding: the attacker's DNS first answers with an allowed address and later with a
    // forbidden one. Every new connection must look up and check the address again.
    [Fact]
    public async Task DnsRebindingIsBlockedOnNextConnection()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        FakeDns dns = new FakeDns(new[] { AllowedAddress }, new[] { ForbiddenAddress });
        using HttpClient client = CreateClient(dns, IsAllowedAddress);
        // Without this the second request would reuse the first connection and skip the check.
        client.DefaultRequestHeaders.ConnectionClose = true;
        string url = $"http://rebinding.test:{server.Port}/ok";

        HttpResponseMessage first = await client.GetAsync(url);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(2, dns.Calls);
        Assert.Equal(1, server.RequestCount);
    }

    // The other half of the rebinding protection: the checked address is the one connected to.
    // A second lookup here would get the forbidden answer and connect somewhere never checked.
    [Fact]
    public async Task ConnectsToCheckedAddressWithoutSecondLookup()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        FakeDns dns = new FakeDns(new[] { AllowedAddress }, new[] { ForbiddenAddress });
        using HttpClient client = CreateClient(dns, IsAllowedAddress);

        HttpResponseMessage response = await client.GetAsync($"http://rebinding.test:{server.Port}/ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, dns.Calls);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task NextAddressIsTriedWhenFirstDoesNotAnswer()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        // The server listens on 127.0.0.1 only, so 127.0.0.2 refuses the connection.
        FakeDns dns = new FakeDns(new[] { ForbiddenAddress, AllowedAddress });
        using HttpClient client = CreateClient(dns, address => true);

        HttpResponseMessage response = await client.GetAsync($"http://two-addresses.test:{server.Port}/ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, server.RequestCount);
    }

    // A rejected IPv6 literal must reach the address rule as the real address, without brackets.
    // The address is loopback on purpose: if the protection ever breaks, this test must still not
    // be able to send anything off this machine.
    [Fact]
    public async Task Ipv6LiteralIsJudgedAsItsOwnAddress()
    {
        List<IPAddress> judged = new List<IPAddress>();
        PublicInternetConnector connector = new PublicInternetConnector(
            Dns.GetHostAddressesAsync,
            address =>
            {
                judged.Add(address);
                return false;
            });
        using HttpClient client = new HttpClient(ProtectedHttpHandler.Create(connector));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync("http://[::1]/"));

        Assert.Equal(new[] { IPAddress.IPv6Loopback }, judged);
    }

    [Fact]
    public async Task ProbeRevealsNoAddressWhenConnectionIsBlocked()
    {
        FakeDns dns = new FakeDns(new[] { AllowedAddress });
        using HttpClient client = CreateClient(dns, UrlSafety.IsPublicAddress);
        HttpHealthProbe probe = new HttpHealthProbe(client);

        ProbeResult result = await probe.CheckAsync("http://looks-public.test/", 5, CancellationToken.None);

        // Without this line the test would also pass if the connector was never used.
        Assert.Equal(1, dns.Calls);
        Assert.Null(result.StatusCode);
        Assert.NotNull(result.FailureReason);
        Assert.DoesNotContain("127.0.0.1", result.FailureReason);
    }

    private static HttpMessageHandler CreateRegisteredHandler(TestApp app)
    {
        IHttpMessageHandlerFactory factory = app.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        // AddHttpClient<IHealthProbe, ...> names the client after the interface. A wrong name gives
        // an unprotected handler, which makes the blocking tests fail instead of passing silently.
        return factory.CreateHandler(nameof(IHealthProbe));
    }

    private static HttpClient CreateClient(FakeDns dns, Func<IPAddress, bool> isAllowedAddress)
    {
        PublicInternetConnector connector = new PublicInternetConnector(dns.ResolveAsync, isAllowedAddress);
        return new HttpClient(ProtectedHttpHandler.Create(connector));
    }

    private static bool IsAllowedAddress(IPAddress address)
    {
        return address.Equals(AllowedAddress);
    }

    // Gives each lookup the next prepared answer, and repeats the last one when they run out.
    private sealed class FakeDns
    {
        private readonly IPAddress[][] _answers;

        public int Calls { get; private set; }

        public FakeDns(params IPAddress[][] answers)
        {
            _answers = answers;
        }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            int index = Math.Min(Calls, _answers.Length - 1);
            Calls++;
            return Task.FromResult(_answers[index]);
        }
    }
}
