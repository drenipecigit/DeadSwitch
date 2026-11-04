using System.Net;
using DeadSwitch.Application.Services;

namespace DeadSwitch.Tests;

// These tests only classify text and addresses. Nothing here makes a lookup or a connection.
public class UrlSafetyTests
{
    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://10.0.0.1")]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://0.0.0.0")]
    [InlineData("http://[::1]")]
    [InlineData("http://[::ffff:127.0.0.1]")]
    [InlineData("http://[::ffff:7f00:1]")]
    [InlineData("http://2130706433")]
    [InlineData("http://127.1")]
    [InlineData("http://127.0.0.1.")]
    [InlineData("http://0x7f000001")]
    [InlineData("http://0177.0.0.1")]
    [InlineData("http://localhost")]
    [InlineData("http://LOCALHOST")]
    [InlineData("http://test.localhost")]
    [InlineData("http://TEST.LOCALHOST")]
    [InlineData("file:///C:/Windows")]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://user@example.com")]
    [InlineData("https://example.com:8080")]
    [InlineData("http://example.com:443:80")]
    [InlineData("https://example.com/#fragment")]
    [InlineData("example.com")]
    [InlineData("/relative/path")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void UnsafeUrlsAreRejected(string? url)
    {
        Assert.Throws<ArgumentException>(() => UrlSafety.Validate(url));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com:443/path?query=1")]
    [InlineData("http://example.com:80")]
    [InlineData("http://8.8.8.8")]
    [InlineData("https://[2606:4700:4700::1111]")]
    public void SafeUrlsAreAccepted(string url)
    {
        Uri uri = UrlSafety.Validate(url);

        Assert.Equal(new Uri(url), uri);
    }

    [Fact]
    public void UrlAtLengthLimitIsAcceptedAndLongerIsRejected()
    {
        string start = "https://example.com/";
        string atLimit = start + new string('a', 2048 - start.Length);

        Assert.NotNull(UrlSafety.Validate(atLimit));
        Assert.Throws<ArgumentException>(() => UrlSafety.Validate(atLimit + "a"));
    }

    // Each range is tested at its first and last address, so a rule that is one step off fails.
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.255.255.255")]
    [InlineData("10.0.0.0")]
    [InlineData("10.255.255.255")]
    [InlineData("100.64.0.0")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("127.0.0.0")]
    [InlineData("127.255.255.255")]
    [InlineData("169.254.0.0")]
    [InlineData("169.254.255.255")]
    [InlineData("172.16.0.0")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.0")]
    [InlineData("192.0.0.255")]
    [InlineData("192.0.2.0")]
    [InlineData("192.0.2.255")]
    [InlineData("192.88.99.0")]
    [InlineData("192.88.99.255")]
    [InlineData("192.168.0.0")]
    [InlineData("192.168.1.1")]
    [InlineData("192.168.255.255")]
    [InlineData("198.18.0.0")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("198.51.100.0")]
    [InlineData("198.51.100.255")]
    [InlineData("203.0.113.0")]
    [InlineData("203.0.113.255")]
    [InlineData("224.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("1fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("2001::")]
    [InlineData("2001:1ff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("2001:db8::")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("2002::")]
    [InlineData("2002:7f00:1::")]
    [InlineData("2002:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("3fff::")]
    [InlineData("3fff:fff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("4000::")]
    public void NonPublicAddressesAreRejected(string ip)
    {
        bool isPublic = UrlSafety.IsPublicAddress(IPAddress.Parse(ip));

        Assert.False(isPublic);
    }

    // The addresses right next to each blocked range, plus ordinary public ones.
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("1.0.0.0")]
    [InlineData("9.255.255.255")]
    [InlineData("11.0.0.0")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("126.255.255.255")]
    [InlineData("128.0.0.0")]
    [InlineData("169.253.255.255")]
    [InlineData("169.255.0.0")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("191.255.255.255")]
    [InlineData("192.0.1.0")]
    [InlineData("192.0.1.255")]
    [InlineData("192.0.3.0")]
    [InlineData("192.1.255.255")]
    [InlineData("192.2.0.0")]
    [InlineData("192.2.1.1")]
    [InlineData("192.3.0.0")]
    [InlineData("192.88.98.255")]
    [InlineData("192.88.100.0")]
    [InlineData("192.167.255.255")]
    [InlineData("192.169.0.0")]
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.0")]
    [InlineData("198.51.99.255")]
    [InlineData("198.51.101.0")]
    [InlineData("203.0.112.255")]
    [InlineData("203.0.114.0")]
    [InlineData("223.255.255.255")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2000::1")]
    [InlineData("2001:200::")]
    [InlineData("2001:db7:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("2001:db9::")]
    [InlineData("2003::")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("3ffe:ffff::")]
    [InlineData("3fff:1000::")]
    public void PublicAddressesAreAllowed(string ip)
    {
        bool isPublic = UrlSafety.IsPublicAddress(IPAddress.Parse(ip));

        Assert.True(isPublic);
    }

    // A trailing dot is the same name in DNS, so it must not slip past the name comparison.
    [Theory]
    [InlineData("http://localhost.")]
    [InlineData("http://test.localhost.")]
    public void LocalhostWithTrailingDotIsRejected(string url)
    {
        Assert.Throws<ArgumentException>(() => UrlSafety.Validate(url));
    }
}
