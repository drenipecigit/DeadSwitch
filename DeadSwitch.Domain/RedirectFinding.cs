namespace DeadSwitch.Domain;

// What one check learned about where the site sends its visitors. A check that learned nothing, such
// as a timeout or an error page, has no finding at all, which is why the field on CheckRecord is nullable.
public enum RedirectFinding
{
    // The site answered with a success. Saved so that a later redirect has something to be compared with.
    NoRedirect,
    SameHost,
    OtherHost,

    // A private address or an internal name. The value is hidden, so it never reaches a third party.
    InternalTarget,

    // The value cannot be interpreted safely, so nothing about it is kept.
    Unreadable,
}
