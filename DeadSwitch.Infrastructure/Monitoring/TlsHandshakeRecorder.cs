using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DeadSwitch.Infrastructure.Monitoring;

// Security-critical: runs inside the certificate validation of every probe connection.
//
// The handshake happens on a connection that is created separately from the request, so nothing ties
// this callback to a particular check. The notes are therefore kept per host, and the probe takes the
// note for its host when the check is over. That is why a note can be missing or belong to another check
// of the same host. A note is only ever a source of a date: it can never make a check fail on its own.
public sealed class TlsHandshakeRecorder
{
    internal const int MaxEntries = 1000;

    // A connection is reused for at most 2 minutes (PooledConnectionLifetime), and a check waits for at
    // most 60 seconds (TimeoutSeconds). A handshake older than that cannot belong to the answer.
    internal static readonly TimeSpan MaxEntryAge = TimeSpan.FromMinutes(3);

    private readonly TimeProvider _clock;
    private readonly object _lock = new object();
    private readonly Dictionary<string, TlsHandshake> _handshakes = new Dictionary<string, TlsHandshake>();

    public TlsHandshakeRecorder(TimeProvider clock)
    {
        _clock = clock;
    }

    // The answer is the framework's own rule when no callback is set, and nothing below may change it.
    public bool OnRemoteCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        bool isValid = errors == SslPolicyErrors.None;

        SslStream? stream = sender as SslStream;
        if (stream != null)
        {
            RecordHandshake(stream.TargetHostName, certificate, isValid);
        }

        return isValid;
    }

    internal void RecordHandshake(string host, X509Certificate? certificate, bool isValid)
    {
        if (!isValid)
        {
            // No date: nothing in a certificate that failed validation can be trusted.
            Store(host, null, true);
            return;
        }

        X509Certificate2? fullCertificate = certificate as X509Certificate2;
        if (fullCertificate == null)
        {
            return;
        }

        DateTime notAfterUtc;
        try
        {
            notAfterUtc = fullCertificate.NotAfter.ToUniversalTime();
        }
        catch (CryptographicException)
        {
            // An exception here would fail the handshake, and only the validation may decide that.
            // The check then simply has no date.
            return;
        }

        Store(host, notAfterUtc, false);
    }

    internal TlsHandshake? Take(string host)
    {
        string key = NormalizeHost(host);

        lock (_lock)
        {
            TlsHandshake? handshake;
            if (!_handshakes.Remove(key, out handshake))
            {
                return null;
            }

            if (IsTooOld(handshake))
            {
                return null;
            }

            return handshake;
        }
    }

    private void Store(string host, DateTime? notAfterUtc, bool isRejected)
    {
        string key = NormalizeHost(host);
        TlsHandshake handshake = new TlsHandshake(notAfterUtc, isRejected, _clock.GetUtcNow());

        lock (_lock)
        {
            // GET /check accepts any host, so without a limit this could grow without end.
            if (!_handshakes.ContainsKey(key) && _handshakes.Count >= MaxEntries)
            {
                RemoveOldEntries();

                if (_handshakes.Count >= MaxEntries)
                {
                    return;
                }
            }

            _handshakes[key] = handshake;
        }
    }

    private void RemoveOldEntries()
    {
        List<string> oldKeys = new List<string>();

        foreach (KeyValuePair<string, TlsHandshake> entry in _handshakes)
        {
            if (IsTooOld(entry.Value))
            {
                oldKeys.Add(entry.Key);
            }
        }

        foreach (string key in oldKeys)
        {
            _handshakes.Remove(key);
        }
    }

    private bool IsTooOld(TlsHandshake handshake)
    {
        return _clock.GetUtcNow() - handshake.SeenAt > MaxEntryAge;
    }

    // Both sides pass through here, so the two ways of writing one host cannot end up as two entries.
    private static string NormalizeHost(string host)
    {
        return host.TrimEnd('.').ToLowerInvariant();
    }
}
