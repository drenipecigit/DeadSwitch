namespace DeadSwitch.Application.Services;

// A positional record, because the values are only passed along and never changed.
public record ServiceConfiguration(
    string Name,
    string Url,
    int IntervalSeconds = 60,
    int TimeoutSeconds = 10,
    bool IsEnabled = true)
{
    // The error messages repeat these numbers on purpose: they are fixed texts that clients can read.
    private const int MaxNameLength = 200;
    private const int MinIntervalSeconds = 10;
    private const int MaxIntervalSeconds = 86400;
    private const int MinTimeoutSeconds = 1;
    private const int MaxTimeoutSeconds = 60;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > MaxNameLength)
        {
            throw new ServiceValidationException("Name must contain 1 to 200 characters.");
        }

        UrlSafety.Validate(Url);

        if (IntervalSeconds < MinIntervalSeconds || IntervalSeconds > MaxIntervalSeconds)
        {
            throw new ServiceValidationException("IntervalSeconds must be between 10 and 86400.");
        }

        if (TimeoutSeconds < MinTimeoutSeconds || TimeoutSeconds > MaxTimeoutSeconds)
        {
            throw new ServiceValidationException("TimeoutSeconds must be between 1 and 60.");
        }
    }
}
