namespace DeadSwitch.Application.Services;

// A positional record, because the values are only passed along and never changed.
public record ServiceConfiguration(
    string Name,
    string Url,
    int IntervalSeconds = 60,
    int TimeoutSeconds = 10,
    bool IsEnabled = true)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 200)
        {
            throw new ServiceValidationException("Name must contain 1 to 200 characters.");
        }

        UrlSafety.Validate(Url);

        if (IntervalSeconds < 10 || IntervalSeconds > 86400)
        {
            throw new ServiceValidationException("IntervalSeconds must be between 10 and 86400.");
        }

        if (TimeoutSeconds < 1 || TimeoutSeconds > 60)
        {
            throw new ServiceValidationException("TimeoutSeconds must be between 1 and 60.");
        }
    }
}
