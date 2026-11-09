using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

// The parser only works on text. Nothing in this file opens a connection or looks up a name, so the
// few public addresses below are only text, needed because the private ranges would not count as public.
public class RedirectTargetParserTests
{
    private const string OwnUrl = "https://monitored.test/";
    private const string OtherHost = "other.example";

    private static readonly Uri OwnAddress = new Uri(OwnUrl);

    [Fact]
    public void NoLocationGivesNoFinding()
    {
        RedirectTargetCheck? result = RedirectTargetParser.Parse(OwnAddress, new List<string>());

        Assert.Null(result);
    }

    [Fact]
    public void AnEmptyLocationGivesNoFinding()
    {
        Assert.Null(Parse(""));
    }

    [Fact]
    public void TwoDifferentLocationsAreUnreadable()
    {
        string[] values = { "https://first.example/", "https://second.example/" };

        RedirectTargetCheck? result = RedirectTargetParser.Parse(OwnAddress, values);

        AssertUnreadable(result);
    }

    [Fact]
    public void TwoIdenticalLocationsAreUnreadable()
    {
        string[] values = { "/same", "/same" };

        RedirectTargetCheck? result = RedirectTargetParser.Parse(OwnAddress, values);

        AssertUnreadable(result);
    }

    [Fact]
    public void AValueAtTheLengthLimitIsRead()
    {
        string value = "/" + new string('a', 2047);

        AssertSameHost(Parse(value));
    }

    [Fact]
    public void AValueOverTheLengthLimitIsUnreadable()
    {
        string value = "/" + new string('a', 2048);

        AssertUnreadable(Parse(value));
    }

    // Written as numbers, so that the source file holds no invisible characters of its own.
    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x0D)]
    [InlineData(0x1F)]
    [InlineData(0x20)]
    [InlineData(0x7F)]
    [InlineData(0x80)]
    [InlineData(0xE9)]
    [InlineData(0x2028)]
    public void ControlCharactersSpacesAndNonAsciiAreUnreadable(int characterCode)
    {
        string value = "https://other.example/a" + (char)characterCode + "b";

        AssertUnreadable(Parse(value));
    }

    [Theory]
    [InlineData(@"/\other.example")]
    [InlineData(@"\\other.example")]
    [InlineData(@"\/other.example")]
    [InlineData(@"https:\\other.example")]
    [InlineData(@"https://monitored.test\@other.example")]
    [InlineData(@"/path\file")]
    public void ABackslashAnywhereIsUnreadable(string value)
    {
        AssertUnreadable(Parse(value));
    }

    // Uri.TryCreate can read "/a" as the file address file:///a on Linux, so the parser must decide this
    // from the characters alone. A run on Windows does not show the Linux behaviour; see the report.
    [Theory]
    [InlineData("/a")]
    [InlineData("a/b")]
    public void APathIsTheSameHostOnEveryOperatingSystem(string value)
    {
        AssertSameHost(Parse(value));
        Assert.True(RedirectTargetParser.IsRelativeReference(value));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("./a")]
    [InlineData("../a")]
    [InlineData("?q=1")]
    [InlineData("#f")]
    [InlineData("/a:b")]
    [InlineData("a/b:c")]
    [InlineData("?a:b")]
    [InlineData("#a:b")]
    public void ARelativeValueIsTheSameHost(string value)
    {
        AssertSameHost(Parse(value));
        Assert.True(RedirectTargetParser.IsRelativeReference(value));
    }

    [Theory]
    [InlineData("//other.example/a")]
    [InlineData("https://other.example/")]
    [InlineData("a:b")]
    public void AValueWithAHostOrSchemeIsNotRelative(string value)
    {
        Assert.False(RedirectTargetParser.IsRelativeReference(value));
    }

    [Fact]
    public void AValueWithoutSchemeButWithTwoSlashesIsAHost()
    {
        AssertOtherHost(Parse("//other.example/a"), OtherHost);
    }

    [Fact]
    public void TheOwnHostWithoutSchemeIsTheSameHost()
    {
        AssertSameHost(Parse("//monitored.test/a"));
    }

    [Theory]
    [InlineData("///other.example")]
    [InlineData("https:///other.example")]
    [InlineData("http:////other.example")]
    [InlineData("https:/other.example")]
    [InlineData("https:other.example")]
    [InlineData("https://")]
    public void FormsWhereABrowserStillFindsAHostAreUnreadable(string value)
    {
        AssertUnreadable(Parse(value));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,a")]
    [InlineData("ftp://other.example/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("mailto:a@other.example")]
    public void OtherSchemesAreUnreadable(string value)
    {
        AssertUnreadable(Parse(value));
    }

    [Theory]
    [InlineData("https://monitored.test@other.example/")]
    [InlineData("https://user:password@other.example/")]
    public void TheHostIsTheOneAfterTheUserinfo(string value)
    {
        AssertOtherHost(Parse(value), OtherHost);
    }

    [Fact]
    public void UserinfoOnTheOwnHostIsTheSameHost()
    {
        AssertSameHost(Parse("https://user:password@monitored.test/"));
    }

    [Fact]
    public void TwoAtSignsBeforeTheHostAreUnreadable()
    {
        AssertUnreadable(Parse("https://a@b@other.example/"));
    }

    [Fact]
    public void AtSignsInThePathDoNotMatter()
    {
        AssertOtherHost(Parse("https://other.example/a@b@c"), OtherHost);
    }

    [Fact]
    public void PunycodeIsKeptAsItIs()
    {
        AssertOtherHost(Parse("https://xn--bcher-kva.example/"), "xn--bcher-kva.example");
    }

    // The u with two dots is written as a number, so that the source file stays plain ASCII.
    [Fact]
    public void AServiceWithAUnicodeNameMatchesTheSameNameInPunycode()
    {
        Uri unicodeAddress = new Uri("https://b" + (char)0xFC + "cher.example/");
        string[] values = { "https://xn--bcher-kva.example/a" };

        RedirectTargetCheck? result = RedirectTargetParser.Parse(unicodeAddress, values);

        AssertSameHost(result);
    }

    [Theory]
    [InlineData("HTTPS://OTHER.EXAMPLE/")]
    [InlineData("https://OTHER.example/")]
    [InlineData("HtTpS://other.example/")]
    [InlineData("https://other.example./")]
    public void CapitalLettersAndATrailingDotAreNormalized(string value)
    {
        AssertOtherHost(Parse(value), OtherHost);
    }

    [Theory]
    [InlineData("http://monitored.test/")]
    [InlineData("https://MONITORED.test./a")]
    [InlineData("https://monitored.test:8443/")]
    [InlineData("https://www.monitored.test/")]
    public void TheOwnHostInAnotherFormIsTheSameHost(string value)
    {
        AssertSameHost(Parse(value));
    }

    [Fact]
    public void ALocationWithoutWwwMatchesAServiceWithWww()
    {
        Uri wwwAddress = new Uri("https://www.monitored.test/");
        string[] values = { "https://monitored.test/" };

        RedirectTargetCheck? result = RedirectTargetParser.Parse(wwwAddress, values);

        AssertSameHost(result);
    }

    [Fact]
    public void WwwIsRemovedFromAnotherHost()
    {
        AssertOtherHost(Parse("https://www.other.example/"), OtherHost);
    }

    [Fact]
    public void WwwIsKeptWhenNoDomainWouldBeLeft()
    {
        AssertOtherHost(Parse("https://www.com/"), "www.com");
    }

    [Fact]
    public void ASubdomainIsAnotherHost()
    {
        AssertOtherHost(Parse("https://login.monitored.test/"), "login.monitored.test");
    }

    [Fact]
    public void AHostAtTheLengthLimitIsKept()
    {
        string host = BuildHostOfLength(253);

        AssertOtherHost(Parse("https://" + host + "/"), host);
    }

    [Fact]
    public void AHostOverTheLengthLimitIsUnreadable()
    {
        string host = BuildHostOfLength(254);

        AssertUnreadable(Parse("https://" + host + "/"));
    }

    [Theory]
    [InlineData("https://a..other.example/")]
    [InlineData("https://other.example../")]
    [InlineData("https://.other.example/")]
    [InlineData("https://other_x.example/")]
    [InlineData("https://ot%20her.example/")]
    [InlineData("https://<b>.example/")]
    [InlineData("https://a*b.example/")]
    public void AMalformedHostIsUnreadable(string value)
    {
        AssertUnreadable(Parse(value));
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/latest")]
    [InlineData("http://[::1]/")]
    [InlineData("http://0x7f.1/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://127.1/")]
    [InlineData("http://0177.0.0.1/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    public void APrivateAddressIsHiddenInEveryForm(string value)
    {
        AssertInternal(Parse(value));
    }

    [Theory]
    [InlineData("https://example.123/")]
    [InlineData("https://1.2.3.4.5/")]
    [InlineData("https://256.256.256.256/")]
    [InlineData("https://example.0x7f/")]
    public void ANumericLastLabelThatIsNoAddressIsUnreadable(string value)
    {
        AssertUnreadable(Parse(value));
    }

    [Fact]
    public void APublicAddressIsKeptInItsShortForm()
    {
        AssertOtherHost(Parse("https://8.8.8.8/"), "8.8.8.8");
    }

    [Fact]
    public void APublicIpv6AddressIsKeptWithoutBrackets()
    {
        AssertOtherHost(Parse("https://[2606:4700:4700::1111]/"), "2606:4700:4700::1111");
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://LOCALHOST./")]
    [InlineData("http://a.localhost/")]
    [InlineData("http://intranet/")]
    [InlineData("http://db.local/")]
    [InlineData("http://db.internal/")]
    [InlineData("http://db.intranet/")]
    [InlineData("http://db.lan/")]
    [InlineData("http://db.corp/")]
    [InlineData("http://db.home/")]
    [InlineData("http://db.home.arpa/")]
    public void AnInternalNameIsHidden(string value)
    {
        AssertInternal(Parse(value));
    }

    [Fact]
    public void NothingButTheHostIsKept()
    {
        RedirectTargetCheck? result = Parse("https://user-marker:password-marker@other.example/path-marker?query-marker#fragment-marker");

        AssertOtherHost(result, OtherHost);
        Assert.DoesNotContain("marker", result!.ToString());
    }

    // Every value that comes out must be safe to show, whatever went in, and nothing may throw.
    [Fact]
    public void AHostThatComesOutHoldsOnlyTheCharactersOfAnAddress()
    {
        string[] hostileValues =
        {
            "https://@everyone/",
            "https://@here.example/",
            "https://other.example/`@everyone`",
            "https://[click](other.example)/",
            "https://<@123456789>.example/",
            "https://*bold*.example/",
            "https://~~a~~.example/",
            "https://||a||.example/",
            "https://a`b.example/",
            "https://other.example:99999/",
            "https://[::1/",
            "https://%00.example/",
            "https://%2e%2e/",
        };

        foreach (string value in hostileValues)
        {
            RedirectTargetCheck? result = Parse(value);

            Assert.NotNull(result);
            AssertHoldsOnlyAddressCharacters(result.Host);
        }
    }

    private static RedirectTargetCheck? Parse(string value)
    {
        string[] values = { value };
        return RedirectTargetParser.Parse(OwnAddress, values);
    }

    private static string BuildHostOfLength(int length)
    {
        string host = "";

        while (host.Length < length - 3)
        {
            host += "a.";
        }

        while (host.Length < length)
        {
            host += "b";
        }

        return host;
    }

    private static void AssertHoldsOnlyAddressCharacters(string? host)
    {
        if (host == null)
        {
            return;
        }

        foreach (char character in host)
        {
            bool isAllowed = char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '.' || character == '-' || character == ':';
            Assert.True(isAllowed, "Character not allowed: " + (int)character);
        }
    }

    private static void AssertSameHost(RedirectTargetCheck? result)
    {
        Assert.NotNull(result);
        Assert.Equal(RedirectFinding.SameHost, result.Finding);
        Assert.Null(result.Host);
    }

    private static void AssertOtherHost(RedirectTargetCheck? result, string host)
    {
        Assert.NotNull(result);
        Assert.Equal(RedirectFinding.OtherHost, result.Finding);
        Assert.Equal(host, result.Host);
    }

    private static void AssertInternal(RedirectTargetCheck? result)
    {
        Assert.NotNull(result);
        Assert.Equal(RedirectFinding.InternalTarget, result.Finding);
        Assert.Null(result.Host);
    }

    private static void AssertUnreadable(RedirectTargetCheck? result)
    {
        Assert.NotNull(result);
        Assert.Equal(RedirectFinding.Unreadable, result.Finding);
        Assert.Null(result.Host);
    }
}
