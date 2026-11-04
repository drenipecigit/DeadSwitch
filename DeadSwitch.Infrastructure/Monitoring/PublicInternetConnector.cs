using System.Net;
using System.Net.Sockets;
using DeadSwitch.Application.Services;

namespace DeadSwitch.Infrastructure.Monitoring;

// Security-critical: the last defence against SSRF, and it runs for every new connection.
// HttpClient calls ConnectAsync instead of opening the connection itself.
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

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        // Checks the real addresses, because a hostname that looks public can point to a private one.
        IPAddress[] addresses = await _resolveHost(context.DnsEndPoint.Host, cancellationToken);

        // The throw at the end of this method would also stop an empty answer today. This check is
        // kept on purpose as defence in depth: it states the intent, and the protection must not
        // depend on how the code further down happens to be written. It is a plain failure and not a
        // blocked destination, because no forbidden address was seen: the name simply has no address.
        if (addresses.Length == 0)
        {
            throw new HttpRequestException("Destination rejected.");
        }

        // All addresses are checked before any connection is tried. One private address is enough
        // to reject the host, because a real public service has no reason to also point to an
        // internal address.
        foreach (IPAddress candidate in addresses)
        {
            if (!_isAllowedAddress(candidate))
            {
                throw new DestinationBlockedException();
            }
        }

        // Connect to the validated IP itself; a second DNS lookup cannot bypass the policy.
        // This is what stops DNS rebinding, where a host answers with a public address
        // for the check and a private address for the connection.
        foreach (IPAddress address in addresses)
        {
            Socket socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            // NoDelay sends small packets at once, which is what HttpClient's own connections do.
            socket.NoDelay = true;

            // "using" cannot be used here: the socket must be closed on failure, but must stay
            // open when it is handed over to the stream.
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);

                // ownsSocket: closing the stream also closes the socket.
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                // This address did not answer, so the loop tries the next one.
                socket.Dispose();
            }
            catch (Exception)
            {
                // Any other failure, such as cancellation, stops the attempt.
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("No destination address could be reached.");
    }
}
