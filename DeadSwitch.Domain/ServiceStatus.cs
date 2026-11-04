namespace DeadSwitch.Domain;

public enum ServiceStatus
{
    // Zero on purpose: a value nobody has set means "not known" and claims nothing about the service.
    Unknown = 0,
    Up,
    Down
}
