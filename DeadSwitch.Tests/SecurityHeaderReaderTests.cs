using System.Net;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Infrastructure.Monitoring;

namespace DeadSwitch.Tests;

// Nothing here is sent anywhere: the addresses are only arguments, and the headers are built by hand.
public class SecurityHeaderReaderTests
{
    private const string EveryNameInOrder =
        "Strict-Transport-Security, Content-Security-Policy, X-Content-Type-Options, X-Frame-Options, Referrer-Policy";

    private static readonly Uri HttpsAddress = new Uri("https://example.test/");
    private static readonly Uri HttpAddress = new Uri("http://example.test/");

    [Fact]
    public void AllFiveHeadersGiveGradeAAndNothingMissing()
    {
        using HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
        TestSecurityHeaders.AddAll(response.Headers, TestSecurityHeaders.OrdinaryValue);

        SecurityHeaderCheck check = SecurityHeaderReader.Read(HttpsAddress, 200, response.Headers);

        Assert.Equal("A", check.Grade);
        Assert.Equal("", check.MissingHeaders);
        Assert.Null(check.NotAssessedReason);
    }

    [Theory]
    [InlineData(TestSecurityHeaders.StrictTransportSecurity)]
    [InlineData(TestSecurityHeaders.ContentSecurityPolicy)]
    [InlineData(TestSecurityHeaders.ContentTypeOptions)]
    [InlineData(TestSecurityHeaders.FrameOptions)]
    [InlineData(TestSecurityHeaders.ReferrerPolicy)]
    public void OneMissingHeaderGivesGradeBAndIsNamed(string missingName)
    {
        using HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
        TestSecurityHeaders.AddAllExcept(response.Headers, missingName);

        SecurityHeaderCheck check = SecurityHeaderReader.Read(HttpsAddress, 200, response.Headers);

        Assert.Equal("B", check.Grade);
        Assert.Equal(missingName, check.MissingHeaders);
    }

    [Fact]
    public void NoHeadersGiveGradeFAndEveryNameInOrder()
    {
        using HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);

        SecurityHeaderCheck check = SecurityHeaderReader.Read(HttpsAddress, 200, response.Headers);

        Assert.Equal("F", check.Grade);
        Assert.Equal(EveryNameInOrder, check.MissingHeaders);
    }

    [Fact]
    public void TheNameOfAHeaderCanBeWrittenInAnyCase()
    {
        using HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
        foreach (string name in TestSecurityHeaders.AllNames)
        {
            response.Headers.TryAddWithoutValidation(name.ToLowerInvariant(), TestSecurityHeaders.OrdinaryValue);
        }

        SecurityHeaderCheck check = SecurityHeaderReader.Read(HttpsAddress, 200, response.Headers);

        Assert.Equal("A", check.Grade);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void AHeaderWithoutARealValueCountsAsMissing(string blankValue)
    {
        using HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
        TestSecurityHeaders.AddAllExcept(response.Headers, TestSecurityHeaders.FrameOptions);
        response.Headers.TryAddWithoutValidation(TestSecurityHeaders.FrameOptions, blankValue);

        SecurityHeaderCheck check = SecurityHeaderReader.Read(HttpsAddress, 200, response.Headers);

        Assert.Equal("B", check.Grade);
        Assert.Equal(TestSecurityHeaders.FrameOptions, check.MissingHeaders);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(299)]
    public void ASuccessOverHttpsIsAssessed(int statusCode)
    {
        using HttpResponseMessage response = new HttpResponseMessage((HttpStatusCode)statusCode);
        TestSecurityHeaders.AddAll(response.Headers, TestSecurityHeaders.OrdinaryValue);

        SecurityHeaderCheck check = SecurityHeaderReader.Read(HttpsAddress, statusCode, response.Headers);

        Assert.Equal("A", check.Grade);
        Assert.Null(check.NotAssessedReason);
    }

    // The headers on a redirect describe the redirect and not the page, so a grade would mislead.
    [Theory]
    [InlineData(300)]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(399)]
    public void ARedirectIsNotAssessed(int statusCode)
    {
        using HttpResponseMessage response = new HttpResponseMessage((HttpStatusCode)statusCode);
        TestSecurityHeaders.AddAll(response.Headers, TestSecurityHeaders.OrdinaryValue);

        SecurityHeaderCheck check = SecurityHeaderReader.Read(HttpsAddress, statusCode, response.Headers);

        AssertNotAssessed(check, SecurityHeaderReader.RedirectReason);
    }

    // An error page often comes from another layer, such as a proxy, and not from the service.
    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public void AnErrorIsNotAssessed(int statusCode)
    {
        using HttpResponseMessage response = new HttpResponseMessage((HttpStatusCode)statusCode);
        TestSecurityHeaders.AddAll(response.Headers, TestSecurityHeaders.OrdinaryValue);

        SecurityHeaderCheck check = SecurityHeaderReader.Read(HttpsAddress, statusCode, response.Headers);

        AssertNotAssessed(check, SecurityHeaderReader.NotSuccessReason);
    }

    [Fact]
    public void AnHttpAddressIsNotAssessedEvenWithAllHeaders()
    {
        using HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
        TestSecurityHeaders.AddAll(response.Headers, TestSecurityHeaders.OrdinaryValue);

        SecurityHeaderCheck check = SecurityHeaderReader.Read(HttpAddress, 200, response.Headers);

        AssertNotAssessed(check, SecurityHeaderReader.NotHttpsReason);
    }

    // The commonest case of all: http://example.com answering with a redirect to https.
    [Fact]
    public void ARedirectFromAnHttpAddressGivesTheHttpReason()
    {
        using HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);

        SecurityHeaderCheck check = SecurityHeaderReader.Read(HttpAddress, 301, response.Headers);

        AssertNotAssessed(check, SecurityHeaderReader.NotHttpsReason);
    }

    // The dashboard shows these texts, so the exact words are part of the contract.
    [Fact]
    public void TheReasonsHaveTheirExactTexts()
    {
        Assert.Equal("Not an HTTPS address.", SecurityHeaderReader.NotHttpsReason);
        Assert.Equal("The response was a redirect.", SecurityHeaderReader.RedirectReason);
        Assert.Equal("The response was not a success.", SecurityHeaderReader.NotSuccessReason);
    }

    [Fact]
    public void AHostileValueNeverReachesTheResult()
    {
        string hostileValue = "<img src=x onerror=alert(1)>";
        using HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
        TestSecurityHeaders.AddAll(response.Headers, hostileValue);

        SecurityHeaderCheck check = SecurityHeaderReader.Read(HttpsAddress, 200, response.Headers);

        Assert.Equal("A", check.Grade);
        Assert.Equal("", check.MissingHeaders);
        Assert.Null(check.NotAssessedReason);
    }

    [Fact]
    public void AVeryLongValueNeverReachesTheResult()
    {
        string longValue = new string('a', 60 * 1024);
        using HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
        TestSecurityHeaders.AddAllExcept(response.Headers, TestSecurityHeaders.ReferrerPolicy);
        response.Headers.TryAddWithoutValidation(TestSecurityHeaders.FrameOptions, longValue);

        SecurityHeaderCheck check = SecurityHeaderReader.Read(HttpsAddress, 200, response.Headers);

        Assert.Equal("B", check.Grade);
        Assert.Equal(TestSecurityHeaders.ReferrerPolicy, check.MissingHeaders);
    }

    private static void AssertNotAssessed(SecurityHeaderCheck check, string expectedReason)
    {
        Assert.Null(check.Grade);
        Assert.Null(check.MissingHeaders);
        Assert.Equal(expectedReason, check.NotAssessedReason);
    }
}
