namespace DeadSwitch.Api.Monitoring;

// Filled from the "Monitoring" section of the configuration.
public sealed class MonitoringOptions
{
    public bool Enabled { get; set; } = true;

    // One second, because a check can only start on a poll. Every interval in whole seconds is
    // then possible; a longer poll would round the real interval up to its next multiple.
    public int PollIntervalSeconds { get; set; } = 1;

    public static bool HasValidPollInterval(MonitoringOptions options)
    {
        return options.PollIntervalSeconds >= 1 && options.PollIntervalSeconds <= 3600;
    }
}
