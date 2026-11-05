namespace DeadSwitch.Domain;

public class MonitoredService
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public ServiceStatus CurrentStatus { get; set; } = ServiceStatus.Unknown;

    // The latest Up or Down, or null when none has been seen for the current URL. CurrentStatus cannot
    // do this job: after Up, Unknown, Down it no longer remembers that the service was up.
    public ServiceStatus? LastKnownStatus { get; set; }

    // DateTimeOffset keeps the UTC offset, so the time means the same thing on every machine.
    public DateTimeOffset? LastCheckedAt { get; set; }

    public int IntervalSeconds { get; set; } = 60;
    public int TimeoutSeconds { get; set; } = 10;

    // True here on purpose, while the database column defaults to false (see DeadSwitchDbContext).
    // New services start enabled, but rows that existed before the column was added stay paused.
    public bool IsEnabled { get; set; } = true;
}
