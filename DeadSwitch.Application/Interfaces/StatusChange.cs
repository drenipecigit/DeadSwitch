using DeadSwitch.Domain;

namespace DeadSwitch.Application.Interfaces;

// A record, because a change is only data: it is never changed after it has been created.
// PreviousStatus is null when nothing was known before: a new service, or a service with a new URL.
public record StatusChange(
    Guid ServiceId,
    string ServiceName,
    string ServiceUrl,
    ServiceStatus? PreviousStatus,
    ServiceStatus NewStatus,
    DateTimeOffset CheckedAt,
    string? FailureReason);
