using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DeadSwitch.Tests;

// Certificates made in memory by the tests. Nothing is added to the Windows certificate store and no
// key is written to the repository. The real validation code runs unchanged: a test only says which
// certificate its own handler trusts, with TrustOnly.
internal static class TestCertificates
{
    public const string MonitoredHost = "monitored.test";

    // What Uri.IdnHost makes of "b\u00FCcher.test".
    public const string IdnMonitoredHost = "xn--bcher-kva.test";

    private const string OrdinarySubject = "CN=DeadSwitch test";

    public static X509Certificate2 CreateForLocalAddress()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return Create(OrdinarySubject, now.AddDays(-1), now.AddDays(30), Array.Empty<string>(), true);
    }

    public static X509Certificate2 CreateExpiredForLocalAddress()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return Create(OrdinarySubject, now.AddDays(-30), now.AddDays(-1), Array.Empty<string>(), true);
    }

    public static X509Certificate2 CreateForOtherName()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string[] names = { "other.test" };
        return Create(OrdinarySubject, now.AddDays(-1), now.AddDays(30), names, false);
    }

    public static X509Certificate2 CreateForMonitoredHosts()
    {
        return CreateForMonitoredHosts(OrdinarySubject);
    }

    public static X509Certificate2 CreateForMonitoredHosts(string subject)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string[] names = { MonitoredHost, IdnMonitoredHost };
        return Create(subject, now.AddDays(-1), now.AddDays(30), names, false);
    }

    // Trusts this certificate and nothing else. Revocation is off because a test certificate has no
    // revocation list to ask.
    public static X509ChainPolicy TrustOnly(X509Certificate2 certificate)
    {
        X509ChainPolicy policy = new X509ChainPolicy();
        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        policy.RevocationMode = X509RevocationMode.NoCheck;
        policy.CustomTrustStore.Add(certificate);
        return policy;
    }

    private static X509Certificate2 Create(
        string subject,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        string[] dnsNames,
        bool forLocalAddress)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder names = new SubjectAlternativeNameBuilder();
        foreach (string dnsName in dnsNames)
        {
            names.AddDnsName(dnsName);
        }

        if (forLocalAddress)
        {
            names.AddIpAddress(IPAddress.Loopback);
        }

        request.CertificateExtensions.Add(names.Build());

        using X509Certificate2 created = request.CreateSelfSigned(notBefore, notAfter);

        // Windows TLS cannot use a key that only exists in memory, so the certificate is loaded again.
        // The key is then stored in the user's profile until the certificate is disposed.
        byte[] exported = created.Export(X509ContentType.Pkcs12);
        return X509CertificateLoader.LoadPkcs12(exported, null);
    }
}
