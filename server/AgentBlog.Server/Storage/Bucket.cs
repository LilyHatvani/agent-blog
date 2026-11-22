using System.Globalization;
using System.Net;

namespace AgentBlog.Storage;

/// <summary>
/// The S3-compatible bucket (VISION principle 4: one service, Postgres, a bucket; Railway's is the reference). Section
/// <c>Blog:Bucket</c>: <c>endpoint</c> (e.g. <c>https://storage.example.com</c>), <c>name</c>, <c>access_key</c>,
/// <c>secret_key</c>, <c>region</c> (default <c>us-east-1</c>; <c>auto</c> for R2-style hosts), <c>url_style</c>
/// (<c>path</c>, the default, or <c>virtual</c>, what Railway's buckets use), <c>max_bytes</c> per file (default 10 MiB)
/// and <c>max_blog_bytes</c> per blog (default 1 GiB). Media is off unless the first four are set (docs/specs/media.md).
/// Lifted from Relay's <c>Storage/Bucket.cs</c> (docs/REUSE.md): the session quotas and download links are Relay's own and
/// stayed there; <see cref="OpenAsync"/> is new, for serving a picture to readers through the blog's own address.
/// </summary>
public sealed partial record BucketSettings
{
    public const long DefaultMaxBytes = 10 * 1024 * 1024;
    public const long DefaultMaxBlogBytes = 1024L * 1024 * 1024;

    public Uri? Endpoint { get; init; }
    public string? Name { get; init; }
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }
    public string Region { get; init; } = "us-east-1";
    public long MaxBytes { get; init; } = DefaultMaxBytes;
    public long MaxBlogBytes { get; init; } = DefaultMaxBlogBytes;
    public BucketUrlStyle UrlStyle { get; init; } = BucketUrlStyle.Path;

    /// <summary>
    /// Where every request for this bucket goes, so also where an agent PUTs a presigned upload: the endpoint's origin
    /// path-style, the bucket's own host under it virtual-hosted. Null when media is off.
    /// </summary>
    public string? Origin => !Enabled ? null : UrlStyle == BucketUrlStyle.Virtual
        ? SigV4QueryPresigner.VirtualHostedOrigin(Endpoint!, Name!).GetLeftPart(UriPartial.Authority)
        : Endpoint!.GetLeftPart(UriPartial.Authority);

    /// <summary>The canonical URI of <paramref name="key"/> at <see cref="Origin"/>: <c>/bucket/key</c> path-style, <c>/key</c> virtual-hosted.</summary>
    public string KeyUri(string key) => UrlStyle == BucketUrlStyle.Virtual
        ? SigV4QueryPresigner.KeyUri(key)
        : SigV4QueryPresigner.PathStyleUri(Name!, key);

    public bool Enabled => Endpoint is not null && !string.IsNullOrEmpty(Name) && !string.IsNullOrEmpty(AccessKey) && !string.IsNullOrEmpty(SecretKey);

    /// <summary>Never the secret key: a record's generated ToString would print it into any log or exception that shows these.</summary>
    public override string ToString() =>
        $"BucketSettings {{ Endpoint = {Endpoint}, Name = {Name}, Region = {Region}, AccessKey = {AccessKey}, SecretKey = {(SecretKey is null ? "(unset)" : "***")}, MaxBytes = {MaxBytes}, MaxBlogBytes = {MaxBlogBytes}, UrlStyle = {UrlStyle} }}";

    public static BucketSettings From(IConfiguration config)
    {
        var s = config.GetSection("Blog:Bucket");
        static string? Value(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        Uri? endpoint = null;
        if (Value(s["endpoint"]) is { } e)
        {
            if (!Uri.TryCreate(e.TrimEnd('/'), UriKind.Absolute, out endpoint) || endpoint.Scheme is not ("https" or "http"))
                throw new InvalidOperationException("Blog:Bucket:endpoint must be an absolute http(s) URL.");
        }
        var max = DefaultMaxBytes;
        if (Value(s["max_bytes"]) is { } m && (!long.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out max) || max < 1))
            throw new InvalidOperationException("Blog:Bucket:max_bytes must be a positive whole number of bytes.");
        var blog = DefaultMaxBlogBytes;
        if (Value(s["max_blog_bytes"]) is { } b && (!long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out blog) || blog < max))
            throw new InvalidOperationException("Blog:Bucket:max_blog_bytes must be a whole number of bytes, at least max_bytes.");
        var style = Value(s["url_style"])?.ToLowerInvariant() switch
        {
            null or "path" => BucketUrlStyle.Path,
            "virtual" => BucketUrlStyle.Virtual,
            _ => throw new InvalidOperationException("Blog:Bucket:url_style must be path or virtual."),
        };
        var name = Value(s["name"]);
        if (style == BucketUrlStyle.Virtual)
        {
            // The bucket becomes part of a host name: a name that is no host label, or an endpoint that is an address,
            // would sign requests to a host no bucket answers at.
            if (name is not null && !DnsBucketName().IsMatch(name))
                throw new InvalidOperationException("Blog:Bucket:url_style virtual needs a bucket name that can be a host name: lower-case letters, digits, '-' and '.'.");
            if (endpoint is not null && endpoint.HostNameType is not UriHostNameType.Dns)
                throw new InvalidOperationException("Blog:Bucket:url_style virtual needs an endpoint with a host name, not an IP address.");
        }
        return new BucketSettings
        {
            Endpoint = endpoint,
            Name = name,
            AccessKey = Value(s["access_key"]),
            SecretKey = Value(s["secret_key"]),
            Region = Value(s["region"]) ?? "us-east-1",
            MaxBytes = max,
            MaxBlogBytes = blog,
            UrlStyle = style,
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial System.Text.RegularExpressions.Regex DnsBucketName();
}

/// <summary>How a request names the bucket: in the path (<c>/bucket/key</c>) or in the host (<c>bucket.host/key</c>).</summary>
public enum BucketUrlStyle { Path, Virtual }

/// <summary>A pre-signed request: what a client sends, exactly, for the bucket to accept it.</summary>
public sealed record PresignedRequest(string Method, string Url, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

/// <summary>An object being read: its bytes as they arrive, and their length when the bucket said.</summary>
public sealed class BucketObject(HttpResponseMessage response, Stream body) : IAsyncDisposable
{
    public Stream Body => body;
    public long? Length => response.Content.Headers.ContentLength;

    public async ValueTask DisposeAsync()
    {
        await body.DisposeAsync();
        response.Dispose();
    }
}

/// <summary>
/// The bucket client: pre-signed PUT (signs Content-Type, Content-Length and the declared SHA-256), GET and DELETE,
/// path-style or virtual-hosted (<see cref="BucketSettings.UrlStyle"/>), by the lifted <see cref="SigV4QueryPresigner"/>.
/// </summary>
public sealed class Bucket(BucketSettings settings, IHttpClientFactory http, TimeProvider clock)
{
    public const string HttpClientName = "blog-bucket";
    /// <summary>Short: on a bucket that ignores the signed checksum, the PUT could replace the bytes until then.</summary>
    public static readonly TimeSpan UploadLifetime = TimeSpan.FromMinutes(15);
    public const string ChecksumHeader = "x-amz-checksum-sha256";

    public bool Enabled => settings.Enabled;
    public long MaxBytes => settings.MaxBytes;
    public long MaxBlogBytes => settings.MaxBlogBytes;

    /// <summary>
    /// The PUT signs the declared hash as <c>x-amz-checksum-sha256</c>: an S3-compatible bucket stores only bytes with that
    /// hash, so the URL cannot replace the upload with other bytes, before confirm or after.
    /// </summary>
    public PresignedRequest Upload(string key, string contentType, long size, byte[] sha256, DateTimeOffset expiresAt)
    {
        var now = clock.GetUtcNow();
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = contentType,
            ["Content-Length"] = size.ToString(CultureInfo.InvariantCulture),
            [ChecksumHeader] = Convert.ToBase64String(sha256),
        };
        var seconds = (int)Math.Max(1, Math.Ceiling((expiresAt - now).TotalSeconds));
        return new PresignedRequest("PUT", Sign("PUT", key, now, seconds, headers), headers, expiresAt);
    }

    /// <summary>
    /// The object's bytes, at most <paramref name="limit"/> + 1 of them (so a bigger object shows as bigger), or null when
    /// the bucket does not hold it. Throws on any other bucket answer.
    /// </summary>
    public async Task<byte[]?> ReadAsync(string key, long limit, CancellationToken ct)
    {
        await using var o = await OpenAsync(key, ct);
        if (o is null) return null;
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length <= limit && (read = await o.Body.ReadAsync(chunk, ct)) > 0) buffer.Write(chunk, 0, read);
        return buffer.ToArray();
    }

    /// <summary>The object as a stream, for passing it on without holding it whole; null when the bucket does not hold it.</summary>
    public async Task<BucketObject?> OpenAsync(string key, CancellationToken ct)
    {
        var url = Sign("GET", key, clock.GetUtcNow(), 60, new Dictionary<string, string>());
        var client = http.CreateClient(HttpClientName);
        var res = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) { res.Dispose(); return null; }
        if (!res.IsSuccessStatusCode)
        {
            res.Dispose();
            throw new HttpRequestException($"The bucket answered GET with {(int)res.StatusCode}.", null, res.StatusCode);
        }
        return new BucketObject(res, await res.Content.ReadAsStreamAsync(ct));
    }

    /// <summary>
    /// Writes bytes the server holds (a picture it fetched from a URL) with a pre-signed PUT like an agent's, its hash
    /// signed too. The PUT signs the length and the SHA-256 up front, so the caller buffers the bytes once.
    /// </summary>
    public async Task WriteAsync(string key, string contentType, ReadOnlyMemory<byte> bytes, byte[] sha256, CancellationToken ct)
    {
        var put = Upload(key, contentType, bytes.Length, sha256, clock.GetUtcNow() + TimeSpan.FromMinutes(5));
        using var client = http.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Put, put.Url) { Content = new ReadOnlyMemoryContent(bytes) };
        req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        req.Content.Headers.ContentLength = bytes.Length;
        req.Headers.TryAddWithoutValidation(ChecksumHeader, put.Headers[ChecksumHeader]);
        using var res = await client.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"The bucket answered PUT with {(int)res.StatusCode}.", null, res.StatusCode);
    }

    /// <summary>Removes the object; true when the bucket no longer holds it (removed now, or already gone).</summary>
    public async Task<bool> DeleteAsync(string key, CancellationToken ct)
    {
        var url = Sign("DELETE", key, clock.GetUtcNow(), 60, new Dictionary<string, string>());
        using var client = http.CreateClient(HttpClientName);
        using var res = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, url), ct);
        return res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.NotFound;
    }

    private string Sign(string method, string key, DateTimeOffset now, int seconds, IReadOnlyDictionary<string, string> headers)
    {
        if (!settings.Enabled) throw new InvalidOperationException("No bucket is configured.");
        return SigV4QueryPresigner.Presign(method, new Uri(settings.Origin!), settings.KeyUri(key),
            settings.Region, "s3", settings.AccessKey!, settings.SecretKey!, now.UtcDateTime, seconds, headers);
    }
}
