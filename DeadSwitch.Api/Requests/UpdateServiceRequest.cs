using DeadSwitch.Application.Services;

namespace DeadSwitch.Api.Requests;

// The JSON body of PUT /services/{id}. Every field is nullable, so a field missing from the JSON
// can be told apart from a real value. POST has its own request, because it has default values.
public record UpdateServiceRequest(string? Name, string? Url, int? IntervalSeconds, int? TimeoutSeconds, bool? IsEnabled)
{
    // PUT replaces the whole service, so a missing field is an error. A default value here could
    // silently enable a paused service or reset its interval.
    public ServiceConfiguration ToConfiguration()
    {
        if (Name == null)
        {
            throw new ServiceValidationException("Name is required when updating a service.");
        }

        if (Url == null)
        {
            throw new ServiceValidationException("Url is required when updating a service.");
        }

        if (IntervalSeconds == null)
        {
            throw new ServiceValidationException("IntervalSeconds is required when updating a service.");
        }

        if (TimeoutSeconds == null)
        {
            throw new ServiceValidationException("TimeoutSeconds is required when updating a service.");
        }

        if (IsEnabled == null)
        {
            throw new ServiceValidationException("IsEnabled is required when updating a service.");
        }

        return new ServiceConfiguration(Name, Url, IntervalSeconds.Value, TimeoutSeconds.Value, IsEnabled.Value);
    }
}
