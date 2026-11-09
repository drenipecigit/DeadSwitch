using System.Net;
using System.Net.Sockets;

namespace DeadSwitch.Tests;

// Takes the place of the connector in tests of the real probe. The rules of UrlSafety only let the
// probe use port 80 or 443 and a public host, while the test server is on 127.0.0.1 and a random port.
// Every connection goes to the local server, whatever the URL says, and no name is ever looked up.
// The connector itself is tested over HTTPS in HttpsConnectionTests.
internal sealed class LocalServerConnectStep
{
    // A field a test can change, to send the next connection to another local server.
    public int Port;

    public LocalServerConnectStep(int port)
    {
        Port = port;
    }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port), cancellationToken);
        return new NetworkStream(socket, ownsSocket: true);
    }
}
