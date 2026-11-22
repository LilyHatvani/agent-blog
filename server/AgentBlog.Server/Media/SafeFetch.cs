using System.Net;
using System.Net.Sockets;

namespace AgentBlog.Media;

/// <summary>An address the server refuses to fetch from: not on the public internet, or not on port 80 or 443.</summary>
public sealed class BlockedAddressException(string message) : Exception(message);

/// <summary>
/// The HTTP client the server uses to fetch a picture from a URL an agent names (docs/specs/media.md § From a URL). The
/// agent chooses the URL, so the server must not become its way into the network the server sits on: the database, the
/// cloud's metadata service, anything on a private address. The check runs where it cannot be dodged, at connect time
/// (<see cref="SocketsHttpHandler.ConnectCallback"/>), on the addresses DNS gave for this very connection, and the
/// socket connects to the address that was checked. So a redirect, a second DNS answer (rebinding), or a name that
/// resolves to 127.0.0.1 all meet the same check. No proxy: a proxy would be what the check sees instead of the target.
/// </summary>
public static class SafeFetch
{
    public const string HttpClientName = "blog-fetch";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    public const int MaxRedirects = 3;

    public static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = true, // each hop opens its connection through ConnectCallback, so each is checked
        MaxAutomaticRedirections = MaxRedirects,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        UseCookies = false,
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var target = context.DnsEndPoint;
        if (target.Port is not (80 or 443))
            throw new BlockedAddressException($"Only ports 80 and 443 are fetched; this address uses {target.Port}.");
        var addresses = IPAddress.TryParse(target.Host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(target.Host, ct);
        if (addresses.Length == 0) throw new BlockedAddressException($"{target.Host} has no address.");
        // Every answer must be public: a name that also answers with a private address is refused, not filtered,
        // so a mixed answer can't be used to probe which internal hosts exist.
        if (addresses.FirstOrDefault(a => !IsPublic(a)) is { } inside)
            throw new BlockedAddressException(literal is not null
                ? $"{literal} is not on the public internet."
                : $"{target.Host} points at {inside}, which is not on the public internet.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, target.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether an address is on the public internet: not loopback, private, shared (CGNAT), link-local (the cloud metadata
    /// service lives at 169.254.169.254), multicast, reserved, documentation or benchmarking space, and for IPv6 only
    /// global unicast (2000::/3) minus the ranges that embed or tunnel an IPv4 address.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0                                   // 0.0.0.0/8 "this network"
                || b[0] == 10                                    // 10.0.0.0/8
                || (b[0] == 100 && (b[1] & 0xC0) == 64)          // 100.64.0.0/10 shared (CGNAT, Railway's private net)
                || b[0] == 127                                   // loopback
                || (b[0] == 169 && b[1] == 254)                  // link-local, cloud metadata
                || (b[0] == 172 && (b[1] & 0xF0) == 16)          // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)       // 192.0.0.0/24 protocol assignments
                || (b[0] == 192 && b[1] == 0 && b[2] == 2)       // TEST-NET-1
                || (b[0] == 192 && b[1] == 88 && b[2] == 99)     // 6to4 relay anycast
                || (b[0] == 192 && b[1] == 168)                  // 192.168.0.0/16
                || (b[0] == 198 && (b[1] & 0xFE) == 18)          // 198.18.0.0/15 benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)    // TEST-NET-2
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)     // TEST-NET-3
                || b[0] >= 224);                                 // multicast, reserved, broadcast
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            if ((b[0] & 0xE0) != 0x20) return false;             // outside 2000::/3: ::1, fc00::/7, fe80::/10, ff00::/8, ...
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // 2001:db8::/32 documentation
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false; // 2001::/32 Teredo
            if (b[0] == 0x20 && b[1] == 0x02) return false;      // 2002::/16 6to4
            return true;
        }
        return false;
    }

    /// <summary>The refusal behind a failed fetch, if the guard was what stopped it.</summary>
    public static BlockedAddressException? Blocked(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
            if (x is BlockedAddressException b) return b;
        return null;
    }
}

/// <summary>
/// What a file's first bytes say it is, of the four picture types the blog keeps. No SVG: it is a document that can carry
/// script, and the blog serves media from its own origin.
/// </summary>
public static class PictureType
{
    public static readonly string[] Allowed = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    public static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    public static string Extension(string contentType) => contentType switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(contentType)),
    };
}
