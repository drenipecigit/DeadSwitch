using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

// Named results for the fake probe, so that a test reads as the list of what the service did.
internal static class TestProbeResults
{
    public const string NothingMissing = "";
    public const string TimedOutReason = "Check timed out.";
    public const string ConnectionFailureReason = "Connection, DNS or TLS failure.";
    public const string UnknownCertificateReason = "A reason that no version has ever written.";

    public static SecurityHeaderCheck HeadersAssessed(string missingHeaders)
    {
        return new SecurityHeaderCheck("B", missingHeaders, null);
    }

    public static SecurityHeaderCheck HeadersNotAssessed()
    {
        return new SecurityHeaderCheck(null, null, "The response was not assessed.");
    }

    public static CertificateCheck CertificateValid(DateTime notAfterUtc)
    {
        return new CertificateCheck(notAfterUtc, null);
    }

    public static CertificateCheck CertificateWithReason(string reason)
    {
        return new CertificateCheck(null, reason);
    }

    public static ProbeResult Response(int statusCode, SecurityHeaderCheck? headers, CertificateCheck? certificate)
    {
        if (statusCode >= 400)
        {
            return new ProbeResult(ServiceStatus.Down, statusCode, 12, "HTTP " + statusCode, headers, certificate);
        }

        return new ProbeResult(ServiceStatus.Up, statusCode, 12, null, headers, certificate);
    }

    public static ProbeResult NoRedirect()
    {
        return new ProbeResult(ServiceStatus.Up, 200, 12, null, null, null, new RedirectTargetCheck(RedirectFinding.NoRedirect, null));
    }

    public static ProbeResult Redirect(RedirectFinding finding, string? host)
    {
        return new ProbeResult(ServiceStatus.Up, 301, 12, null, HeadersNotAssessed(), null, new RedirectTargetCheck(finding, host));
    }

    public static ProbeResult RedirectToOtherHost(string host)
    {
        return Redirect(RedirectFinding.OtherHost, host);
    }

    public static ProbeResult Assessed(string missingHeaders)
    {
        return Response(200, HeadersAssessed(missingHeaders), null);
    }

    public static ProbeResult Timeout()
    {
        return new ProbeResult(ServiceStatus.Down, null, 10000, TimedOutReason);
    }

    public static ProbeResult ConnectionFailure()
    {
        return new ProbeResult(ServiceStatus.Down, null, 12, ConnectionFailureReason);
    }

    public static ProbeResult Blocked()
    {
        return new ProbeResult(ServiceStatus.Unknown, null, 12, "Destination address blocked by policy.");
    }

    public static ProbeResult Rejected()
    {
        return new ProbeResult(ServiceStatus.Down, null, 12, ConnectionFailureReason, null, CertificateWithReason(CertificateReasons.NotValid));
    }

    public static ProbeResult NotSeen()
    {
        return Response(200, null, CertificateWithReason(CertificateReasons.NoNewHandshake));
    }

    public static ProbeResult UnknownCertificateText()
    {
        return Response(200, null, CertificateWithReason(UnknownCertificateReason));
    }

    // Every kind of answer where the headers are not assessed. A text, because a test row must be a constant.
    public static ProbeResult HeadersNotAssessedBy(string kind)
    {
        if (kind == "timeout")
        {
            return Timeout();
        }

        if (kind == "connection failure")
        {
            return ConnectionFailure();
        }

        if (kind == "blocked")
        {
            return Blocked();
        }

        if (kind == "redirect")
        {
            return Response(301, HeadersNotAssessed(), null);
        }

        if (kind == "client error")
        {
            return Response(404, HeadersNotAssessed(), null);
        }

        if (kind == "server error")
        {
            return Response(500, HeadersNotAssessed(), null);
        }

        throw new ArgumentException("Unknown kind of answer: " + kind);
    }
}
