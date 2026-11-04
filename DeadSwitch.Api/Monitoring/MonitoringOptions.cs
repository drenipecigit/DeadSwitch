namespace DeadSwitch.Api.Monitoring;

// Filled from the "Monitoring" section of the configuration.
public sealed class MonitoringOptions
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 5;

    public static bool HasValidPollInterval(MonitoringOptions options)
    {
        return options.PollIntervalSeconds >= 1 && options.PollIntervalSeconds <= 3600;
    }
}
