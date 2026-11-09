namespace DeadSwitch.Domain;

// What one check found out about the certificate. Only Valid and Rejected are real verdicts.
public enum CertificateFinding
{
    // The certificate was accepted and its date is known.
    Valid,

    // The check's own TLS failure, together with a rejected handshake.
    Rejected,

    // A response came, but no handshake could be read. Says nothing about the certificate.
    NotSeen,

    // No certificate was looked at: a timeout, a connection failure, http, an old row or an unknown text.
    NotLookedAt,
}
