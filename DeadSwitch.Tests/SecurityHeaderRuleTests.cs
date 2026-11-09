using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

public class SecurityHeaderRuleTests
{
    [Theory]
    [InlineData(0, "A")]
    [InlineData(1, "B")]
    [InlineData(2, "C")]
    [InlineData(3, "D")]
    [InlineData(4, "F")]
    [InlineData(5, "F")]
    public void TheGradeFollowsTheNumberOfMissingHeaders(int missingCount, string expectedGrade)
    {
        Assert.Equal(expectedGrade, SecurityHeaderRule.GradeFor(missingCount));
    }

    [Fact]
    public void ExactlyTheFiveHeadersAreCheckedInAFixedOrder()
    {
        Assert.Equal(TestSecurityHeaders.AllNames, SecurityHeaderRule.CheckedHeaders);
    }

    [Fact]
    public void AHeaderMissingNowButNotBeforeHasDisappeared()
    {
        List<string> disappeared = SecurityHeaderRule.FindDisappeared("", TestSecurityHeaders.FrameOptions);

        Assert.Equal(new string[] { TestSecurityHeaders.FrameOptions }, disappeared);
    }

    [Fact]
    public void AHeaderMissingBothTimesHasNotDisappeared()
    {
        List<string> disappeared = SecurityHeaderRule.FindDisappeared(TestSecurityHeaders.FrameOptions, TestSecurityHeaders.FrameOptions);

        Assert.Empty(disappeared);
    }

    [Fact]
    public void AHeaderThatCameBackHasNotDisappeared()
    {
        List<string> disappeared = SecurityHeaderRule.FindDisappeared(TestSecurityHeaders.FrameOptions, "");

        Assert.Empty(disappeared);
    }

    [Fact]
    public void NothingMissingEitherTimeGivesNothing()
    {
        Assert.Empty(SecurityHeaderRule.FindDisappeared("", ""));
    }

    [Fact]
    public void SeveralHeadersAreGivenInTheFixedOrder()
    {
        string missing = TestSecurityHeaders.ReferrerPolicy + SecurityHeaderRule.Separator + TestSecurityHeaders.StrictTransportSecurity;

        List<string> disappeared = SecurityHeaderRule.FindDisappeared("", missing);

        Assert.Equal(new string[] { TestSecurityHeaders.StrictTransportSecurity, TestSecurityHeaders.ReferrerPolicy }, disappeared);
    }

    [Fact]
    public void AllFiveCanDisappearAtOnce()
    {
        string all = string.Join(SecurityHeaderRule.Separator, TestSecurityHeaders.AllNames);

        Assert.Equal(TestSecurityHeaders.AllNames, SecurityHeaderRule.FindDisappeared("", all));
    }

    // A name inside a longer text is not that header. Matching parts of a text would report this one.
    [Fact]
    public void OnlyWholeNamesCount()
    {
        string longerText = "My-" + TestSecurityHeaders.FrameOptions + "-Copy";

        Assert.Empty(SecurityHeaderRule.FindDisappeared("", longerText));
    }

    [Fact]
    public void ATextThatIsNotAHeaderNameIsIgnored()
    {
        string planted = "<@1> planted text" + SecurityHeaderRule.Separator + TestSecurityHeaders.FrameOptions;

        Assert.Equal(new string[] { TestSecurityHeaders.FrameOptions }, SecurityHeaderRule.FindDisappeared("", planted));
    }
}
