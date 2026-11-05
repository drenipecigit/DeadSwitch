using System.Net;
using System.Net.Sockets;

namespace DeadSwitch.Application.Services;

// Security-critical: protects against SSRF, where a caller makes this server call its own
// internal network. UrlSafetyTests tests every range below at its first and last address.
public static class UrlSafety
{
    private const int MaxUrlLength = 2048;

    // First line of defence, used when a URL is registered. It cannot be the only one:
    // a hostname that looks public can still resolve to a private address.
    // PublicInternetConnector therefore checks the real addresses when connecting.
    public static Uri Validate(string? url)
    {
        Uri? uri = ParseAllowedForm(url);
        if (uri == null)
        {
            throw new ServiceValidationException("Url must be an absolute HTTP(S) URL on port 80 or 443, without credentials or fragment.");
        }

        if (!HasPublicHost(uri))
        {
            throw new ServiceValidationException("Url must address the public Internet.");
        }

        return uri;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        // ::ffff:127.0.0.1 is really 127.0.0.1, so it must be judged by the IPv4 rules.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        byte[] bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsPublicIPv4(bytes);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IsPublicIPv6(bytes);
        }

        return false;
    }

    // Returns null when the URL is not acceptable.
    private static Uri? ParseAllowedForm(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength)
        {
            return null;
        }

        Uri? uri;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        // Credentials and fragments have no use in a health check and can hide the real host.
        if (uri.UserInfo != "" || uri.Fragment != "")
        {
            return null;
        }

        // Only ports 80 and 443 are allowed, so the API cannot be used to scan other ports.
        if (uri.Port != 80 && uri.Port != 443)
        {
            return null;
        }

        if (uri.HostNameType == UriHostNameType.Unknown)
        {
            return null;
        }

        return uri;
    }

    private static bool HasPublicHost(Uri uri)
    {
        string? connectionHost = GetConnectionHost(uri);
        if (connectionHost == null)
        {
            return false;
        }

        // Uri has already rewritten odd forms: "http://2130706433" has become 127.0.0.1 here.
        // The brackets an IPv6 host is written with are removed, so only the address is compared.
        // A trailing dot is the same name in DNS: "localhost." is "localhost".
        string host = connectionHost.Trim('[', ']').TrimEnd('.');

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Always tried on the converted text, and never decided from Uri.HostNameType. Uri calls the
        // fullwidth form of 127.0.0.1 a name, and only after the conversion is it plainly an address.
        IPAddress? address;
        if (IPAddress.TryParse(host, out address))
        {
            return IsPublicAddress(address);
        }

        // An ordinary hostname. Its real addresses are checked when connecting.
        return true;
    }

    // Security-critical: IdnHost is the name the connection really uses, so this is the name to judge.
    // In DnsSafeHost a fullwidth "localhost" is only an unfamiliar name, which would pass this check.
    // A host that cannot be converted would throw later inside HttpClient, so it is rejected here.
    // Returns null when the host cannot be converted.
    private static string? GetConnectionHost(Uri uri)
    {
        try
        {
            return uri.IdnHost;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    // Every rule below excludes a range that is not the public Internet.
    private static bool IsPublicIPv4(byte[] bytes)
    {
        byte first = bytes[0];
        byte second = bytes[1];
        byte third = bytes[2];

        // 0.0.0.0/8: "this network".
        if (first == 0)
        {
            return false;
        }

        // 10.0.0.0/8: private.
        if (first == 10)
        {
            return false;
        }

        // 100.64.0.0/10: shared address space used inside carrier networks.
        if (first == 100 && second >= 64 && second <= 127)
        {
            return false;
        }

        // 127.0.0.0/8: this machine.
        if (first == 127)
        {
            return false;
        }

        // 169.254.0.0/16: link-local, including the metadata service of many cloud hosts.
        if (first == 169 && second == 254)
        {
            return false;
        }

        // 172.16.0.0/12: private.
        if (first == 172 && second >= 16 && second <= 31)
        {
            return false;
        }

        // 192.0.0.0/24: special-purpose.
        if (first == 192 && second == 0 && third == 0)
        {
            return false;
        }

        // 192.0.2.0/24: documentation.
        if (first == 192 && second == 0 && third == 2)
        {
            return false;
        }

        // 192.88.99.0/24: old relay.
        if (first == 192 && second == 88 && third == 99)
        {
            return false;
        }

        // 192.168.0.0/16: private.
        if (first == 192 && second == 168)
        {
            return false;
        }

        // 198.18.0.0/15: benchmarking.
        if (first == 198 && (second == 18 || second == 19))
        {
            return false;
        }

        // 198.51.100.0/24: documentation.
        if (first == 198 && second == 51 && third == 100)
        {
            return false;
        }

        // 203.0.113.0/24: documentation.
        if (first == 203 && second == 0 && third == 113)
        {
            return false;
        }

        // 224 and up: multicast and reserved.
        if (first >= 224)
        {
            return false;
        }

        return true;
    }

    private static bool IsPublicIPv6(byte[] bytes)
    {
        byte first = bytes[0];
        byte second = bytes[1];
        byte third = bytes[2];
        byte fourth = bytes[3];

        // 2000::/3 is the only range used for public IPv6 addresses. This is an allowlist on
        // purpose: every range outside it, known or not, is rejected by default.
        if (first < 0x20 || first > 0x3f)
        {
            return false;
        }

        // 2001::/23: special-purpose, including Teredo tunnels that can carry IPv4 addresses.
        if (first == 0x20 && second == 0x01 && third < 0x02)
        {
            return false;
        }

        // 2001:db8::/32: documentation.
        if (first == 0x20 && second == 0x01 && third == 0x0d && fourth == 0xb8)
        {
            return false;
        }

        // 2002::/16: 6to4 tunnels, which can hide a private IPv4 address inside.
        if (first == 0x20 && second == 0x02)
        {
            return false;
        }

        // 3fff::/20: documentation.
        if (first == 0x3f && second == 0xff && third <= 0x0f)
        {
            return false;
        }

        return true;
    }
}
