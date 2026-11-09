namespace DeadSwitch.Application.Interfaces;

// The result of looking at a response's security headers. It holds our own fixed texts only and
// never a value from the server. Grade and MissingHeaders are null when the response was not assessed.
public record SecurityHeaderCheck(string? Grade, string? MissingHeaders, string? NotAssessedReason);
