using DeadSwitch.Domain;

namespace DeadSwitch.Application.Interfaces;

// Security: there is no field for the value the server sent, so that value has nowhere to travel.
// Host is set only for OtherHost, and has then passed the allowlist in RedirectTargetParser.
public record RedirectTargetCheck(RedirectFinding Finding, string? Host);
