namespace DeadSwitch.Domain;

// Saved in the history and read back to decide on warnings. Changing a text here would make the
// rows already saved look like an unknown reason, so they must stay as they are.
public static class CertificateReasons
{
    public const string NotValid = "The certificate was not valid.";
    public const string NoNewHandshake = "No new TLS handshake for this check.";
}
