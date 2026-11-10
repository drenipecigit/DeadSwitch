using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

public class DownConfirmationRuleTests
{
    // The owner chose two checks. A change of the number must be a decision, not an accident.
    [Fact]
    public void DownNeedsTwoFailedChecksInARow()
    {
        Assert.Equal(2, DownConfirmationRule.FailedChecksBeforeDown);
    }

    [Theory]
    [InlineData(0, ServiceStatus.Up, 0)]
    [InlineData(3, ServiceStatus.Up, 0)]
    [InlineData(0, ServiceStatus.Down, 1)]
    [InlineData(1, ServiceStatus.Down, 2)]
    [InlineData(5, ServiceStatus.Down, 6)]
    // Unknown means that no request was sent, so the count neither grows nor starts over.
    [InlineData(0, ServiceStatus.Unknown, 0)]
    [InlineData(1, ServiceStatus.Unknown, 1)]
    public void NextFailedChecksInARowFollowsTheTable(int current, ServiceStatus newStatus, int expected)
    {
        int result = DownConfirmationRule.NextFailedChecksInARow(current, newStatus);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(ServiceStatus.Up, 0, ServiceStatus.Up)]
    [InlineData(ServiceStatus.Down, 1, null)]
    [InlineData(ServiceStatus.Down, 2, ServiceStatus.Down)]
    [InlineData(ServiceStatus.Down, 3, ServiceStatus.Down)]
    [InlineData(ServiceStatus.Unknown, 0, null)]
    [InlineData(ServiceStatus.Unknown, 2, null)]
    public void ConfirmedStatusFollowsTheTable(ServiceStatus newStatus, int failedChecksInARow, ServiceStatus? expected)
    {
        ServiceStatus? result = DownConfirmationRule.ConfirmedStatus(newStatus, failedChecksInARow);

        Assert.Equal(expected, result);
    }
}
