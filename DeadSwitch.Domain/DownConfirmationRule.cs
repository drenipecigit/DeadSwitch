namespace DeadSwitch.Domain;

// Decides when a failed check is enough to call a service DOWN. A single timeout happens even when
// nothing is wrong, and it would give one DOWN and one UP alarm for a problem that never existed.
public static class DownConfirmationRule
{
    public const int FailedChecksBeforeDown = 2;

    public static int NextFailedChecksInARow(int current, ServiceStatus newStatus)
    {
        if (newStatus == ServiceStatus.Up)
        {
            return 0;
        }

        if (newStatus == ServiceStatus.Down)
        {
            return current + 1;
        }

        // Unknown means that no request was sent, so nothing was learned about the service. The row
        // of failed checks neither grows nor starts over.
        return current;
    }

    // Null means that nothing is confirmed yet, and then nothing may change what is known.
    public static ServiceStatus? ConfirmedStatus(ServiceStatus newStatus, int failedChecksInARow)
    {
        if (newStatus == ServiceStatus.Up)
        {
            // Up is confirmed at once: the service answered, so waiting would only delay good news.
            return ServiceStatus.Up;
        }

        if (newStatus == ServiceStatus.Down && failedChecksInARow >= FailedChecksBeforeDown)
        {
            return ServiceStatus.Down;
        }

        return null;
    }
}
