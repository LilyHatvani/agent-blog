using AgentKit.Fetch;

namespace AgentBlog.Media;

/// <summary>
/// The HTTP client the server uses to fetch a picture from a URL an agent names (docs/specs/media.md § From a URL). The
/// agent chooses the URL, so the server must not become its way into the network it sits on. The guard is agent-kit's
/// (<see cref="GuardedHttp"/>, one table of public addresses shared with Relay): checked at connect time on the
/// addresses DNS gave for that connection, ports 80 and 443 only, up to three redirects, each through the same check.
/// </summary>
public static class SafeFetch
{
    public const string HttpClientName = "blog-fetch";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    public const int MaxRedirects = 3;

    public static SocketsHttpHandler CreateHandler() =>
        GuardedHttp.CreateHandler(new GuardedHttpOptions { Ports = [80, 443], FollowRedirects = true, MaxRedirects = MaxRedirects });

    /// <summary>The refusal behind a failed fetch, if the guard was what stopped it.</summary>
    public static BlockedAddressException? Blocked(Exception e) => GuardedHttp.Blocked(e);
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
