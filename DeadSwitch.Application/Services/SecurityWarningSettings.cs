namespace DeadSwitch.Application.Services;

// Filled from the "SecurityWarnings" section of the configuration.
public sealed class SecurityWarningSettings
{
    // The error message in Program.cs repeats these numbers.
    private const int MinWarningDays = 1;
    private const int MaxWarningDays = 365;

    // Warn when this many days or fewer are left of a certificate.
    public int CertificateWarningDays { get; set; } = 14;

    public static bool HasValidWarningDays(SecurityWarningSettings settings)
    {
        return settings.CertificateWarningDays >= MinWarningDays && settings.CertificateWarningDays <= MaxWarningDays;
    }
}
