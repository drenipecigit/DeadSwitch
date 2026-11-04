using System.Net;
using System.Net.Sockets;

namespace DeadSwitch.Application.Services;

// Security-critical: protects against SSRF, where a caller makes this server call its own
// internal network. The logic is deliberately left untouched and is not yet in the project's
// plain style: the real connection path has no tests, so a rewrite could weaken the protection
// without any test noticing. It will be simplified once those tests exist.
public static class UrlSafety
{
    // First line of defence, used when a URL is registered. It cannot be the only one:
    // a hostname that looks public can still resolve to a private address.
    // PublicInternetConnector therefore checks the real addresses when connecting.
    public static Uri Validate(string? url)
    {
        // Only ports 80 and 443 are allowed, so the API cannot be used to scan other ports.
        // Credentials and fragments have no use in a health check and can hide the real host.
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Port is not (80 or 443) || uri.HostNameType == UriHostNameType.Unknown)
            throw new ArgumentException("Url must be an absolute HTTP(S) URL on port 80 or 443, without credentials or fragment.");

        // Uri has already rewritten odd forms: "http://2130706433" has become 127.0.0.1 here.
        // Trim removes the brackets an IPv6 host is written with, so only the address is compared.
        var host = uri.DnsSafeHost.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host, out var address) && !IsPublicAddress(address)))
            throw new ArgumentException("Url must address the public Internet.");
        return uri;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        // ::ffff:127.0.0.1 is really 127.0.0.1, so it must be judged by the IPv4 rules below.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // Every line below excludes a range that is not the public Internet.
            // 0.x "this network", 10.x private, 127.x this machine.
            return bytes[0] is not (0 or 10 or 127) &&
                // 100.64.0.0/10: shared address space used inside carrier networks.
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                // 169.254.0.0/16: link-local, including the metadata service of many cloud hosts.
                !(bytes[0] == 169 && bytes[1] == 254) &&
                // 172.16.0.0/12: private.
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                // 192.168.x private, 192.0.x special-purpose and documentation, 192.88.99.x old relay.
                // Known issue: 192.0.x and 192.2.x are wider than intended and also block public addresses.
                !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 ||
                    (bytes[1] == 2) || (bytes[1] == 88 && bytes[2] == 99))) &&
                // 198.18.0.0/15 benchmarking, 198.51.100.0/24 documentation.
                !(bytes[0] == 198 && (bytes[1] is 18 or 19 || (bytes[1] == 51 && bytes[2] == 100))) &&
                // 203.0.113.0/24: documentation.
                !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) &&
                // 224 and up: multicast and reserved.
                bytes[0] < 224;
        }
        // Only global unicast IPv6; exclude special-purpose/documentation/tunnel ranges.
        // An allowlist on purpose: every IPv6 range not named here is rejected by default.
        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            // 2000::/3: the only range used for public IPv6 addresses.
            (bytes[0] & 0xe0) == 0x20 &&
            // 2001::/23: special-purpose, including Teredo tunnels that can carry IPv4 addresses.
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 0x02) &&
            // 2001:db8::/32: documentation.
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) &&
            // 2002::/16: 6to4 tunnels, which can hide a private IPv4 address inside.
            !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
            // 3fff::/20: documentation.
            !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
    }
}
