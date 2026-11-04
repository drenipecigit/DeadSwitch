using DeadSwitch.Application.Services;

namespace DeadSwitch.Api.Requests;

public record RegisterServiceRequest(string Name, string Url, int IntervalSeconds = 60,
    int TimeoutSeconds = 10, bool IsEnabled = true)
{
    public ServiceConfiguration ToConfiguration() => new(Name, Url, IntervalSeconds, TimeoutSeconds, IsEnabled);
}
