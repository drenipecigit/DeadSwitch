using DeadSwitch.Application.Services;

namespace DeadSwitch.Api.Requests;

// The JSON body of POST and PUT /services. Kept apart from ServiceConfiguration so the shape of
// the HTTP request can change without touching the Application layer.
// The default values decide what a field missing from the JSON becomes.
public record RegisterServiceRequest(
    string Name,
    string Url,
    int IntervalSeconds = 60,
    int TimeoutSeconds = 10,
    bool IsEnabled = true)
{
    public ServiceConfiguration ToConfiguration()
    {
        return new ServiceConfiguration(Name, Url, IntervalSeconds, TimeoutSeconds, IsEnabled);
    }
}
