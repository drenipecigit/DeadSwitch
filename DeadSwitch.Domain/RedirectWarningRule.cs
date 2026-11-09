namespace DeadSwitch.Domain;

public static class RedirectWarningRule
{
    // A target seen this recently gives no new warning. A site that switches between two targets would
    // otherwise send a message on every switch, while a target that has been gone for long warns again.
    public const int RememberDays = 30;

    // Whether the visitor is sent away from the monitored site, which is what a warning is about.
    public static bool IsExternal(RedirectFinding? finding)
    {
        if (finding == RedirectFinding.OtherHost)
        {
            return true;
        }

        if (finding == RedirectFinding.InternalTarget)
        {
            return true;
        }

        return finding == RedirectFinding.Unreadable;
    }
}
