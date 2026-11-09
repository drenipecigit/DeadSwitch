using System.Net;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Monitoring;

namespace DeadSwitch.Tests;

// Real responses through the real SocketsHttpHandler. Every connection goes to 127.0.0.1, and every name
// is answered by the small fake below, so nothing can leave the machine even if a redirect were followed.
public class RedirectTargetHandlerTests
{
    private const string NameLookupWithoutConnector = "NameLookupWithoutConnector";
    private const string MonitoredName = "monitored.test";
    private const string ProbeUrl = "http://monitored.test/";

    private static readonly IPAddress LocalAddress = IPAddress.Parse("127.0.0.1");

    [Fact]
    [Trait("Category", NameLookupWithoutConnector)]
    public async Task TheTargetIsNeverContactedAndItsNameIsNeverLookedUp()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        RecordingDns dns = new RecordingDns();
        using HttpClient client = CreateClient(dns);
        Uri address = new Uri($"http://{MonitoredName}:{server.Port}/redirect-to-other-name");

        using HttpResponseMessage response = await client.GetAsync(address);
        RedirectTargetCheck? result = RedirectTargetReader.Read(address, (int)response.StatusCode, response.Headers);

        Assert.NotNull(result);
        Assert.Equal(RedirectFinding.OtherHost, result.Finding);
        Assert.Equal("other-name.test", result.Host);
        Assert.Equal(0, server.CountRequestsTo("/secret"));
        Assert.Equal(new string[] { MonitoredName }, dns.GetHosts());
    }

    [Fact]
    [Trait("Category", NameLookupWithoutConnector)]
    public async Task ARelativeLocationFromARealServerIsTheSameHost()
    {
        await using LocalTestServer server = await LocalTestServer.StartAsync();
        using HttpClient client = CreateClient(new RecordingDns());
        Uri address = new Uri($"http://{MonitoredName}:{server.Port}/redirect");

        using HttpResponseMessage response = await client.GetAsync(address);
        RedirectTargetCheck? result = RedirectTargetReader.Read(address, (int)response.StatusCode, response.Headers);

        Assert.NotNull(result);
        Assert.Equal(RedirectFinding.SameHost, result.Finding);
        Assert.Equal(0, server.CountRequestsTo("/secret"));
    }

    [Fact]
    public async Task AnOrdinaryRawRedirectIsReadByTheRealProbe()
    {
        await using RawHttpTestServer server = RawHttpTestServer.StartRedirect("Location: https://other.test/\r\n");

        ProbeResult result = await CheckThroughRealHandlerAsync(server);

        Assert.Equal(ServiceStatus.Up, result.Status);
        AssertTarget(result, RedirectFinding.OtherHost, "other.test");
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task TwoRawLocationHeadersAreUnreadable()
    {
        await using RawHttpTestServer server = RawHttpTestServer.StartRedirect("Location: https://first.test/\r\nLocation: https://second.test/\r\n");

        ProbeResult result = await CheckThroughRealHandlerAsync(server);

        Assert.Equal(ServiceStatus.Up, result.Status);
        AssertTarget(result, RedirectFinding.Unreadable, null);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task AFoldedRawLocationIsUnreadable()
    {
        await using RawHttpTestServer server = RawHttpTestServer.StartRedirect("Location: https://other.test/a\r\n b\r\n");

        ProbeResult result = await CheckThroughRealHandlerAsync(server);

        Assert.Equal(ServiceStatus.Up, result.Status);
        AssertTarget(result, RedirectFinding.Unreadable, null);
    }

    // Written as numbers, so that the source file holds no invisible characters of its own.
    [Theory]
    [InlineData(0x01)]
    [InlineData(0x7F)]
    [InlineData(0xE9)]
    public async Task ARawControlOrHighByteIsUnreadable(int byteValue)
    {
        await using RawHttpTestServer server = RawHttpTestServer.StartRedirect("Location: https://other.test/a" + (char)byteValue + "b\r\n");

        ProbeResult result = await CheckThroughRealHandlerAsync(server);

        Assert.Equal(ServiceStatus.Up, result.Status);
        AssertTarget(result, RedirectFinding.Unreadable, null);
        Assert.Equal(1, server.Connections);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x0D)]
    public async Task ARawNulOrCarriageReturnIsUnreadableAndNeverKept(int byteValue)
    {
        await using RawHttpTestServer server = RawHttpTestServer.StartRedirect("Location: https://other.test/secret-marker" + (char)byteValue + "b\r\n");

        ProbeResult result = await CheckThroughRealHandlerAsync(server);

        Assert.Equal(ServiceStatus.Up, result.Status);
        AssertTarget(result, RedirectFinding.Unreadable, null);
        Assert.DoesNotContain("secret-marker", result.ToString());
        Assert.Equal(1, server.Connections);
    }

    // A bare line feed ends the header line, so what follows is another header and not part of the value.
    [Fact]
    public async Task ARawLineFeedEndsTheHeader()
    {
        await using RawHttpTestServer server = RawHttpTestServer.StartRedirect("Location: https://other.test/\nX-Injected: secret-marker\r\n");

        ProbeResult result = await CheckThroughRealHandlerAsync(server);

        Assert.Equal(ServiceStatus.Up, result.Status);
        AssertTarget(result, RedirectFinding.OtherHost, "other.test");
        Assert.DoesNotContain("secret-marker", result.ToString());
        Assert.Equal(1, server.Connections);
    }

    private static async Task<ProbeResult> CheckThroughRealHandlerAsync(RawHttpTestServer server)
    {
        LocalServerConnectStep connectStep = new LocalServerConnectStep(server.Port);
        RecordingDns dns = new RecordingDns();
        PublicInternetConnector connector = new PublicInternetConnector(dns.ResolveAsync, AllowOnlyLocalAddress);

        SocketsHttpHandler handler = ProtectedHttpHandler.Create(connector);
        handler.ConnectCallback = connectStep.ConnectAsync;

        using HttpClient client = new HttpClient(handler);
        HttpHealthProbe probe = new HttpHealthProbe(client, new TlsHandshakeRecorder(TimeProvider.System));
        return await probe.CheckAsync(ProbeUrl, 5, CancellationToken.None);
    }

    private static HttpClient CreateClient(RecordingDns dns)
    {
        PublicInternetConnector connector = new PublicInternetConnector(dns.ResolveAsync, AllowOnlyLocalAddress);
        return new HttpClient(ProtectedHttpHandler.Create(connector));
    }

    private static bool AllowOnlyLocalAddress(IPAddress address)
    {
        return address.Equals(LocalAddress);
    }

    private static void AssertTarget(ProbeResult result, RedirectFinding finding, string? host)
    {
        Assert.NotNull(result.RedirectTarget);
        Assert.Equal(finding, result.RedirectTarget.Finding);
        Assert.Equal(host, result.RedirectTarget.Host);
    }

    // Answers every name with 127.0.0.1 and remembers which names it was asked about.
    private sealed class RecordingDns
    {
        private readonly object _sync = new object();
        private readonly List<string> _hosts = new List<string>();

        public List<string> GetHosts()
        {
            lock (_sync)
            {
                return new List<string>(_hosts);
            }
        }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _hosts.Add(host);
            }

            IPAddress[] addresses = { LocalAddress };
            return Task.FromResult(addresses);
        }
    }
}
