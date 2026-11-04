namespace DeadSwitch.Domain;

public class CheckRecord
{
    public Guid Id { get; set; }
    public Guid MonitoredServiceId { get; set; }
    public string Url { get; set; } = string.Empty;
    public DateTime CheckedAtUtc { get; set; }
    public string Status { get; set; } = "DOWN";
    public int? StatusCode { get; set; }
    public long ResponseTimeMs { get; set; }
    public string? FailureReason { get; set; }
}
