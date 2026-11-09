using System.Net;
using System.Security.Cryptography.X509Certificates;
using DeadSwitch.Infrastructure.Monitoring;

namespace DeadSwitch.Tests;

// A client built like the probe's own, which only reaches the local server and only trusts the
// certificate the test names. The connector is there because the handler requires one, and its rule
// refuses everything: the connection step below replaces it.
internal static class TlsTestClient
{
    public static HttpClient Create(LocalServerConnectStep connectStep, TlsHandshakeRecorder recorder, X509Certificate2 trustedCertificate)
    {
        PublicInternetConnector unused = new PublicInternetConnector(AnswerWithNoAddress, RefuseEveryAddress);
        SocketsHttpHandler handler = ProtectedHttpHandler.Create(unused, recorder);
        handler.ConnectCallback = connectStep.ConnectAsync;
        handler.SslOptions.CertificateChainPolicy = TestCertificates.TrustOnly(trustedCertificate);
        return new HttpClient(handler);
    }

    private static Task<IPAddress[]> AnswerWithNoAddress(string host, CancellationToken cancellationToken)
    {
        return Task.FromResult(Array.Empty<IPAddress>());
    }

    private static bool RefuseEveryAddress(IPAddress address)
    {
        return false;
    }
}
