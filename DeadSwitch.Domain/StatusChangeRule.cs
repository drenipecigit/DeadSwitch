namespace DeadSwitch.Domain;

// Decides which status changes are worth a notification. Here it can be tested without a database.
public static class StatusChangeRule
{
    public static bool ShouldNotify(ServiceStatus? lastKnownStatus, ServiceStatus newStatus)
    {
        // Unknown means that no request was sent, so nothing was learned about the service.
        if (newStatus == ServiceStatus.Unknown)
        {
            return false;
        }

        if (lastKnownStatus == ServiceStatus.Up)
        {
            return newStatus == ServiceStatus.Down;
        }

        if (lastKnownStatus == ServiceStatus.Down)
        {
            return newStatus == ServiceStatus.Up;
        }

        // Nothing is known yet: a new service, or a service with a new URL. Down must still notify,
        // or a misspelled URL that is down from its first check would never give an alarm.
        return newStatus == ServiceStatus.Down;
    }
}
