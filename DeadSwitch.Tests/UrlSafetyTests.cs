using System.Net;
using DeadSwitch.Application.Services;

namespace DeadSwitch.Tests;

public class UrlSafetyTests
{
    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://10.0.0.1")]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://[::1]")]
    [InlineData("http://[::ffff:127.0.0.1]")]
    [InlineData("http://2130706433")]
    [InlineData("http://localhost")]
    [InlineData("http://test.localhost")]
    [InlineData("file:///C:/Windows")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com:8080")]
    [InlineData("https://example.com/#fragment")]
    public void UnsafeUrlsAreRejected(string url)
    {
        Assert.Throws<ArgumentException>(() => UrlSafety.Validate(url));
    }

    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("172.31.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:7f00:1::")]
    public void NonPublicAddressesAreRejected(string ip)
    {
        bool isPublic = UrlSafety.IsPublicAddress(IPAddress.Parse(ip));

        Assert.False(isPublic);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void PublicAddressesAreAllowed(string ip)
    {
        bool isPublic = UrlSafety.IsPublicAddress(IPAddress.Parse(ip));

        Assert.True(isPublic);
    }
}
