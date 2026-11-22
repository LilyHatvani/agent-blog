using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentBlog.Storage;

/// <summary>
/// AWS Signature Version 4 for a presigned URL, the query-string form
/// (https://docs.aws.amazon.com/AmazonS3/latest/API/sigv4-query-string-auth.html). Lifted from Relay (<c>Relay.Server/Storage/SigV4QueryPresigner.cs</c>, which lifted it from EchoOfDreams
/// <c>EchoOfDreams.Core/Services/SigV4QueryPresigner.cs</c>; docs/REUSE.md): built by hand because an SDK
/// presigner cannot sign <c>Content-Length</c>. Every header named in <c>signedHeaders</c> is part of the signature: a
/// request that sends a different value for any of them is refused by the bucket. Pure and clock-free (the caller
/// passes <c>now</c>), so it is pinned by AWS's own published example in the tests.
/// </summary>
public static class SigV4QueryPresigner
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>The payload hash a presigned URL signs: the body is not part of the signature.</summary>
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    /// <summary>
    /// The presigned URL for <paramref name="method"/> on <paramref name="canonicalUri"/> (already path-encoded,
    /// starting with <c>/</c>) at <paramref name="origin"/>. <paramref name="signedHeaders"/> must not name <c>host</c>
    /// (it is always signed, from the origin's host and port); names are case-insensitive, values signed trimmed.
    /// <paramref name="extraQuery"/> adds signed query parameters, such as S3's <c>response-content-disposition</c>.
    /// </summary>
    public static string Presign(
        string method, Uri origin, string canonicalUri, string region, string service,
        string accessKey, string secretKey, DateTime nowUtc, int expiresSeconds,
        IReadOnlyDictionary<string, string> signedHeaders, IReadOnlyDictionary<string, string>? extraQuery = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(signedHeaders);
        // Written with a literal 'Z': a local time would sign a moment hours away from the one the bucket checks.
        if (nowUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("The signing time must be UTC.", nameof(nowUtc));
        if (!canonicalUri.StartsWith('/')) throw new ArgumentException("The canonical URI starts with '/'.", nameof(canonicalUri));
        if (expiresSeconds is < 1 or > 604_800)
            throw new ArgumentOutOfRangeException(nameof(expiresSeconds), "SigV4 presigned URLs live 1 s to 7 days.");

        var amzDate = nowUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = nowUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var scope = $"{dateStamp}/{region}/{service}/aws4_request";

        // Host as the client sends it: with the port only when it isn't the scheme's default.
        var host = origin.IsDefaultPort ? origin.Host : $"{origin.Host}:{origin.Port}";
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = host };
        foreach (var (name, value) in signedHeaders)
        {
            var lower = name.Trim().ToLowerInvariant();
            if (lower == "host") throw new ArgumentException("host is always signed from the origin; don't pass it.", nameof(signedHeaders));
            headers[lower] = value.Trim();
        }
        var signedHeaderList = string.Join(';', headers.Keys);

        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = Algorithm,
            ["X-Amz-Credential"] = $"{accessKey}/{scope}",
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = expiresSeconds.ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = signedHeaderList,
        };
        foreach (var (name, value) in extraQuery ?? new Dictionary<string, string>())
            if (!query.TryAdd(name, value)) throw new ArgumentException($"{name} is set by the signer.", nameof(extraQuery));
        var canonicalQuery = string.Join('&', query.Select(kv => $"{UriEncode(kv.Key)}={UriEncode(kv.Value)}"));

        var canonicalRequest = string.Join('\n',
            method,
            canonicalUri,
            canonicalQuery,
            string.Concat(headers.Select(kv => $"{kv.Key}:{kv.Value}\n")),
            signedHeaderList,
            UnsignedPayload);

        var stringToSign = string.Join('\n',
            Algorithm,
            amzDate,
            scope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, service);
        var kSigning = Hmac(kService, "aws4_request");
        var signature = Hex(Hmac(kSigning, stringToSign));

        var authority = origin.GetLeftPart(UriPartial.Authority);
        return $"{authority}{canonicalUri}?{canonicalQuery}&X-Amz-Signature={signature}";
    }

    /// <summary>
    /// SigV4's URI encoding: every byte except <c>A-Z a-z 0-9 - . _ ~</c> is <c>%XX</c> (uppercase hex), <c>/</c>
    /// included, so this is for query names and values and for single path segments.
    /// </summary>
    public static string UriEncode(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~')
                builder.Append(c);
            else
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>A path-style canonical URI: <c>/{bucket}/{key}</c>, each segment encoded on its own.</summary>
    public static string PathStyleUri(string bucket, string key) => "/" + UriEncode(bucket) + KeyUri(key);

    /// <summary>A virtual-hosted canonical URI: <c>/{key}</c>, each segment encoded on its own (the bucket is in the host).</summary>
    public static string KeyUri(string key) => "/" + string.Join('/', key.Split('/').Select(UriEncode));

    /// <summary>
    /// The virtual-hosted origin of <paramref name="bucket"/> at <paramref name="endpoint"/>: the same scheme and port,
    /// the host <c>{bucket}.{endpoint host}</c>.
    /// </summary>
    public static Uri VirtualHostedOrigin(Uri endpoint, string bucket) =>
        new UriBuilder(endpoint.Scheme, $"{bucket}.{endpoint.Host}", endpoint.IsDefaultPort ? -1 : endpoint.Port).Uri;

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
