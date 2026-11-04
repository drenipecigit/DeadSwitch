using System.Net;
using System.Net.Sockets;
using DeadSwitch.Application.Services;

namespace DeadSwitch.Infrastructure.Monitoring;

// Security-critical: the last defence against SSRF, and it runs for every new connection.
// HttpClient calls this method instead of opening the connection itself.
// The logic is deliberately left untouched and is not yet in the project's plain style:
// it has no tests, so a rewrite could weaken the protection without any test noticing.
internal sealed class PublicInternetConnector
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHost;
    private readonly Func<IPAddress, bool> _isAllowedAddress;

    // The DNS lookup and the address rule are parameters so the tests can run the real connection
    // code against a local server. Production always passes the rules in ProtectedHttpHandler.Create.
    public PublicInternetConnector(
        Func<string, CancellationToken, Task<IPAddress[]>> resolveHost,
        Func<IPAddress, bool> isAllowedAddress)
    {
        _resolveHost = resolveHost;
        _isAllowedAddress = isAllowedAddress;
    }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        // Checks the real addresses, because a hostname that looks public can point to a private one.
        var addresses = await _resolveHost(context.DnsEndPoint.Host, ct);
        // One private address is enough to reject the host, because a real public service
        // has no reason to also point to an internal address.
        if (addresses.Length == 0 || addresses.Any(ip => !_isAllowedAddress(ip)))
            throw new HttpRequestException("Destination rejected.");

        // Connect to the validated IP itself; a second DNS lookup cannot bypass the policy.
        // This is what stops DNS rebinding, where a host answers with a public address
        // for the check and a private address for the connection.
        foreach (var address in addresses)
        {
            // NoDelay sends small packets at once, which is what HttpClient's own connections do.
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                // ownsSocket: closing the stream also closes the socket.
                return new NetworkStream(socket, ownsSocket: true);
            }
            // This address did not answer, so the loop tries the next one.
            catch (SocketException) { socket.Dispose(); }
            // Any other failure, such as cancellation, stops the attempt. The socket must still be closed.
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("No destination address could be reached.");
    }
}
