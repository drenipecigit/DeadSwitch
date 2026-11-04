namespace DeadSwitch.Tests;

// A clock the tests can move by hand, so "wait for the interval" takes no real time.
internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        return Now;
    }
}
