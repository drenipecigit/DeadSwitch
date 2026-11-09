using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Monitoring;

namespace DeadSwitch.Tests;

// The reader decides which responses say anything about a redirect. Nothing here uses the network.
public class RedirectTargetReaderTests
{
    private const string LocationHeader = "Location";
    private const string OtherLocation = "https://other.example/";

    private static readonly Uri OwnAddress = new Uri("https://monitored.test/");

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(299)]
    public void ASuccessIsNoRedirect(int statusCode)
    {
        using HttpResponseMessage response = new HttpResponseMessage();

        RedirectTargetCheck? result = RedirectTargetReader.Read(OwnAddress, statusCode, response.Headers);

        Assert.NotNull(result);
        Assert.Equal(RedirectFinding.NoRedirect, result.Finding);
        Assert.Null(result.Host);
    }

    // 201 Created also uses this header, and there it does not mean that the visitor is sent away.
    [Fact]
    public void ALocationOnASuccessIsIgnored()
    {
        using HttpResponseMessage response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation(LocationHeader, OtherLocation);

        RedirectTargetCheck? result = RedirectTargetReader.Read(OwnAddress, 201, response.Headers);

        Assert.NotNull(result);
        Assert.Equal(RedirectFinding.NoRedirect, result.Finding);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(500)]
    public void AnErrorSaysNothing(int statusCode)
    {
        using HttpResponseMessage response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation(LocationHeader, OtherLocation);

        RedirectTargetCheck? result = RedirectTargetReader.Read(OwnAddress, statusCode, response.Headers);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(304)]
    [InlineData(399)]
    public void ARedirectWithoutLocationSaysNothing(int statusCode)
    {
        using HttpResponseMessage response = new HttpResponseMessage();

        RedirectTargetCheck? result = RedirectTargetReader.Read(OwnAddress, statusCode, response.Headers);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    [InlineData(399)]
    public void ARedirectWithLocationIsRead(int statusCode)
    {
        using HttpResponseMessage response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation(LocationHeader, OtherLocation);

        RedirectTargetCheck? result = RedirectTargetReader.Read(OwnAddress, statusCode, response.Headers);

        Assert.NotNull(result);
        Assert.Equal(RedirectFinding.OtherHost, result.Finding);
        Assert.Equal("other.example", result.Host);
    }

    [Fact]
    public void TwoLocationHeadersAreBothSeen()
    {
        using HttpResponseMessage response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation(LocationHeader, "/first");
        response.Headers.TryAddWithoutValidation(LocationHeader, "/second");

        RedirectTargetCheck? result = RedirectTargetReader.Read(OwnAddress, 302, response.Headers);

        Assert.NotNull(result);
        Assert.Equal(RedirectFinding.Unreadable, result.Finding);
    }

    // The typed property would return nothing for this value, and a broken redirect would look like none.
    [Fact]
    public void AValueTheFrameworkCannotParseIsStillSeen()
    {
        using HttpResponseMessage response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation(LocationHeader, "https://other example/");

        RedirectTargetCheck? result = RedirectTargetReader.Read(OwnAddress, 302, response.Headers);

        Assert.NotNull(result);
        Assert.Equal(RedirectFinding.Unreadable, result.Finding);
    }
}
