namespace DeadSwitch.Domain;

public class MonitoredService
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public ServiceStatus CurrentStatus { get; set; } = ServiceStatus.Unknown;

    // The latest confirmed Up or Down, or null when none has been confirmed for the current URL. Down is
    // only confirmed after several failed checks in a row (see DownConfirmationRule). CurrentStatus
    // cannot do this job: it shows every single check, and after Up, Unknown, Down it no longer
    // remembers that the service was up.
    public ServiceStatus? LastKnownStatus { get; set; }

    // Down checks in a row for the current URL. Saved in the database and not kept in memory, so a
    // restart neither forgets a failed check nor turns one into an alarm.
    public int FailedChecksInARow { get; set; }

    // DateTimeOffset keeps the UTC offset, so the time means the same thing on every machine.
    public DateTimeOffset? LastCheckedAt { get; set; }

    public int IntervalSeconds { get; set; } = 60;
    public int TimeoutSeconds { get; set; } = 10;

    // True here on purpose, while the database column defaults to false (see DeadSwitchDbContext).
    // New services start enabled, but rows that existed before the column was added stay paused.
    public bool IsEnabled { get; set; } = true;
}
