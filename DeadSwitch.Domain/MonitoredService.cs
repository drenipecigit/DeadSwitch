namespace DeadSwitch.Domain;

public class MonitoredService
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? CurrentStatus { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
}