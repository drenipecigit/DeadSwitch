using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeadSwitch.Tests;

// Answers every connection on 127.0.0.1 with exactly the bytes it was given. A normal web server refuses
// to send a broken header, and only broken bytes show what the real handler does with them.
internal sealed class RawHttpTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private readonly byte[] _response;
    private Task _acceptLoop = Task.CompletedTask;
    private int _connections;

    public int Port { get; private set; }

    public int Connections
    {
        get { return Volatile.Read(ref _connections); }
    }

    private RawHttpTestServer(byte[] response)
    {
        _response = response;
    }

    // Latin-1 turns each char into exactly one byte, so a test can send any byte it wants.
    public static RawHttpTestServer StartRedirect(string headerLines)
    {
        string text = "HTTP/1.1 302 Found\r\n" + headerLines + "Content-Length: 0\r\nConnection: close\r\n\r\n";

        RawHttpTestServer server = new RawHttpTestServer(Encoding.Latin1.GetBytes(text));
        server._listener.Start();
        server.Port = ((IPEndPoint)server._listener.LocalEndpoint).Port;
        server._acceptLoop = server.AcceptConnectionsAsync();
        return server;
    }

    private async Task AcceptConnectionsAsync()
    {
        try
        {
            while (true)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                Interlocked.Increment(ref _connections);
                await AnswerAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
            // The test is over, so the server stops listening.
        }
    }

    private async Task AnswerAsync(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[4096];
        string received = "";

        while (!received.Contains("\r\n\r\n"))
        {
            int count = await stream.ReadAsync(buffer, _stop.Token);
            if (count == 0)
            {
                return;
            }

            received += Encoding.Latin1.GetString(buffer, 0, count);
        }

        await stream.WriteAsync(_response, _stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _acceptLoop;
        _listener.Stop();
        _stop.Dispose();
    }
}
