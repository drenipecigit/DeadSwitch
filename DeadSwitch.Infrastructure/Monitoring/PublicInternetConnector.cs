using System.Net;
using System.Net.Sockets;
using DeadSwitch.Application.Services;

namespace DeadSwitch.Infrastructure.Monitoring;

public static class PublicInternetConnector
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(ip => !UrlSafety.IsPublicAddress(ip)))
            throw new HttpRequestException("Destination rejected.");

        // Connect to the validated IP itself; a second DNS lookup cannot bypass the policy.
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("No destination address could be reached.");
    }
}
