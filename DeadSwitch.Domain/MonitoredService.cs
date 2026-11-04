namespace DeadSwitch.Domain;

public class MonitoredService
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? CurrentStatus { get; set; } = "UNKNOWN";
    public DateTimeOffset? LastCheckedAt { get; set; }
    public int IntervalSeconds { get; set; } = 60;
    public int TimeoutSeconds { get; set; } = 10;
    public bool IsEnabled { get; set; } = true;
}
