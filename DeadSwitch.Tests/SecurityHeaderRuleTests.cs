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
}
