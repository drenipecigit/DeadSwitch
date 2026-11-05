using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

public class StatusChangeRuleTests
{
    // Every combination is listed, so the whole rule can be read here as a table.
    [Theory]
    [InlineData(ServiceStatus.Up, ServiceStatus.Down, true)]
    [InlineData(ServiceStatus.Down, ServiceStatus.Up, true)]
    [InlineData(ServiceStatus.Up, ServiceStatus.Up, false)]
    [InlineData(ServiceStatus.Down, ServiceStatus.Down, false)]
    [InlineData(ServiceStatus.Up, ServiceStatus.Unknown, false)]
    [InlineData(ServiceStatus.Down, ServiceStatus.Unknown, false)]
    [InlineData(null, ServiceStatus.Down, true)]
    [InlineData(null, ServiceStatus.Up, false)]
    [InlineData(null, ServiceStatus.Unknown, false)]
    // The code never saves Unknown as the last known status. If a row holds it anyway, it counts as null.
    [InlineData(ServiceStatus.Unknown, ServiceStatus.Down, true)]
    [InlineData(ServiceStatus.Unknown, ServiceStatus.Up, false)]
    [InlineData(ServiceStatus.Unknown, ServiceStatus.Unknown, false)]
    public void ShouldNotifyFollowsTheTable(ServiceStatus? lastKnownStatus, ServiceStatus newStatus, bool expected)
    {
        bool result = StatusChangeRule.ShouldNotify(lastKnownStatus, newStatus);

        Assert.Equal(expected, result);
    }
}
