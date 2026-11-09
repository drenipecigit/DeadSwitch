using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

public class RedirectWarningRuleTests
{
    [Theory]
    [InlineData(RedirectFinding.OtherHost)]
    [InlineData(RedirectFinding.InternalTarget)]
    [InlineData(RedirectFinding.Unreadable)]
    public void AFindingThatLeavesTheSiteIsExternal(RedirectFinding finding)
    {
        Assert.True(RedirectWarningRule.IsExternal(finding));
    }

    [Theory]
    [InlineData(RedirectFinding.NoRedirect)]
    [InlineData(RedirectFinding.SameHost)]
    public void AFindingThatStaysOnTheSiteIsNotExternal(RedirectFinding finding)
    {
        Assert.False(RedirectWarningRule.IsExternal(finding));
    }

    [Fact]
    public void NoFindingIsNotExternal()
    {
        Assert.False(RedirectWarningRule.IsExternal(null));
    }

    [Fact]
    public void ATargetIsRememberedForThirtyDays()
    {
        Assert.Equal(30, RedirectWarningRule.RememberDays);
    }
}
