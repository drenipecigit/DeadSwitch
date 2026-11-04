using System.Net;
using System.Net.Sockets;

namespace DeadSwitch.Application.Services;

public record ServiceConfiguration(string Name, string Url, int IntervalSeconds = 60,
    int TimeoutSeconds = 10, bool IsEnabled = true)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 200)
            throw new ArgumentException("Name must contain 1 to 200 characters.");
        UrlSafety.Validate(Url);
        if (IntervalSeconds is < 10 or > 86400)
            throw new ArgumentException("IntervalSeconds must be between 10 and 86400.");
        if (TimeoutSeconds is < 1 or > 60)
            throw new ArgumentException("TimeoutSeconds must be between 1 and 60.");
    }
}

public static class UrlSafety
{
    public static Uri Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Port is not (80 or 443) || uri.HostNameType == UriHostNameType.Unknown)
            throw new ArgumentException("Url must be an absolute HTTP(S) URL on port 80 or 443, without credentials or fragment.");

        var host = uri.DnsSafeHost.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host, out var address) && !IsPublicAddress(address)))
            throw new ArgumentException("Url must address the public Internet.");
        return uri;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] is not (0 or 10 or 127) &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 ||
                    (bytes[1] == 2) || (bytes[1] == 88 && bytes[2] == 99))) &&
                !(bytes[0] == 198 && (bytes[1] is 18 or 19 || (bytes[1] == 51 && bytes[2] == 100))) &&
                !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) &&
                bytes[0] < 224;
        }
        // Only global unicast IPv6; exclude special-purpose/documentation/tunnel ranges.
        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            (bytes[0] & 0xe0) == 0x20 &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 0x02) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
            !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
    }
}
