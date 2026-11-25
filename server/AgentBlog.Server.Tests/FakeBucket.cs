using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace AgentBlog.Server.Tests;

// Lifted from Relay's Relay.Server.Tests/RelayApp.cs (docs/REUSE.md), unchanged but for the names.

/// <summary>
/// An S3-compatible bucket over HTTP, path-style, holding objects in memory: PUT stores the body, HEAD answers its
/// Content-Length, GET returns it (with <c>response-content-disposition</c> as S3 applies it), DELETE removes it, 404 for
/// anything it does not hold. Every request must be pre-signed (SigV4 query form) and carry every header it signed; it does
/// not recompute signatures, which the presigner's own tests pin against AWS's published example. Like S3, a PUT with
/// <c>x-amz-checksum-sha256</c> is stored only when the body has that hash (400 BadDigest otherwise). Like a lax bucket,
/// it stores whatever length it is sent, so the server's check of bucket truth is what a test sees;
/// <see cref="StoreIgnoringChecks"/> is a bucket that ignores checksums too.
/// Path-style by default (<c>https://bucket.test/blog-test/key</c>); with <c>virtualHosted</c>, a bucket that answers
/// only at its own host (<c>https://blog-test.bucket.test/key</c>), as Railway's do, and refuses path-style requests.
/// </summary>
public sealed class FakeBucket(bool virtualHosted = false) : HttpMessageHandler
{
    /// <summary>The bucket's own origin when it is virtual-hosted.</summary>
    public const string VirtualHostedOrigin = "https://" + MediaHost.BucketName + ".bucket.test";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _objects = new();

    /// <summary>An HTTP client that reaches the stub, for a test acting as the uploading session.</summary>
    public HttpClient Client() => new(this, disposeHandler: false);

    /// <summary>
    /// The object a request URL names, as <c>/blog-test/key</c> whichever the style, or null when this bucket does not
    /// answer at that address: another host, or the other URL style.
    /// </summary>
    private string? ObjectOf(Uri uri)
    {
        var authority = uri.GetLeftPart(UriPartial.Authority);
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (virtualHosted) return authority == VirtualHostedOrigin && path.Length > 1 ? "/" + MediaHost.BucketName + path : null;
        return authority == MediaHost.BucketEndpoint && path.StartsWith("/" + MediaHost.BucketName + "/", StringComparison.Ordinal) ? path : null;
    }

    private string ObjectOf(string url) =>
        ObjectOf(new Uri(url)) ?? throw new ArgumentException($"{url} is not an address of this bucket.", nameof(url));

    /// <summary>What a bucket that checks no checksum would hold after a PUT of <paramref name="bytes"/> to this URL.</summary>
    public void StoreIgnoringChecks(string url, byte[] bytes) => _objects[ObjectOf(url)] = bytes;

    /// <summary>Whether the bucket holds an object at this (pre-signed or plain) URL's key.</summary>
    public bool Holds(string url) => _objects.ContainsKey(ObjectOf(url));

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _failDeletes = new();

    /// <summary>A bucket having trouble: a DELETE of this URL's key answers 503 until <see cref="DeleteWorks"/>.</summary>
    public void DeleteFails(string url) => _failDeletes[ObjectOf(url)] = true;

    public void DeleteWorks(string url) => _failDeletes.TryRemove(ObjectOf(url), out _);
    /// <summary>What the bucket holds under a key, or null.</summary>
    public byte[]? Held(string key) => _objects.TryGetValue($"/{MediaHost.BucketName}/{key}", out var b) ? b : null;

    /// <summary>The object vanishes, as if removed out of band.</summary>
    public void Remove(string key) => _objects.TryRemove($"/{MediaHost.BucketName}/{key}", out _);

    /// <summary>The next <paramref name="times"/> GETs of this key answer 503, as a bucket having a bad minute.</summary>
    public void FailGets(string key, int times) => _failures[$"/{MediaHost.BucketName}/{key}"] = times;

    /// <summary>How many of the failures <see cref="FailGets"/> set are still to come.</summary>
    public int FailuresLeft(string key) => _failures.TryGetValue($"/{MediaHost.BucketName}/{key}", out var n) ? n : 0;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _failures = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _storeThenFail = new();

    /// <summary>A bucket that stores a PUT under this key prefix, then answers 500: bytes held that the server never heard landed.</summary>
    public void StoreThenFailPuts(string keyPrefix) => _storeThenFail[$"/{MediaHost.BucketName}/{keyPrefix}"] = true;

    private (HttpStatusCode Status, string Body)? _refusePuts;

    /// <summary>A bucket that refuses every PUT with this status and S3 error body, storing nothing (an R2 that won't take a header).</summary>
    public void RefusePuts(HttpStatusCode status, string body) => _refusePuts = (status, body);

    /// <summary>PUTs are taken again.</summary>
    public void AcceptPuts() => _refusePuts = null;

    /// <summary>The keys the bucket holds under a prefix.</summary>
    public List<string> KeysUnder(string keyPrefix) =>
        _objects.Keys.Where(k => k.StartsWith($"/{MediaHost.BucketName}/{keyPrefix}", StringComparison.Ordinal))
            .Select(k => k[(MediaHost.BucketName.Length + 2)..]).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var signed = query["X-Amz-SignedHeaders"]?.Split(';') ?? [];
        if (ObjectOf(uri) is not { } key || query["X-Amz-Signature"] is null
            || query["X-Amz-Algorithm"] != "AWS4-HMAC-SHA256" || !signed.Contains("host"))
            return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
        string? Header(string name) =>
            request.Headers.TryGetValues(name, out var v) ? v.First()
            : request.Content?.Headers.TryGetValues(name, out var c) == true ? c.First()
            : null;
        // A signed header missing from the request fails the signature. Content-Length is the body's own.
        if (signed.Any(h => h is not ("host" or "content-length") && Header(h) is null))
            return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
        if (request.Method == HttpMethod.Get && _failures.TryGetValue(key, out var left) && left > 0)
        {
            _failures[key] = left - 1;
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        }
        if (request.Method == HttpMethod.Put)
        {
            if (_refusePuts is { } refused)
                return new HttpResponseMessage(refused.Status) { Content = new StringContent(refused.Body, System.Text.Encoding.UTF8, "application/xml") };
            if (!signed.Contains("content-length") || !signed.Contains("content-type"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            if (Header("x-amz-checksum-sha256") is { } checksum && checksum != Convert.ToBase64String(SHA256.HashData(body)))
                return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("<Error><Code>BadDigest</Code></Error>") };
            _objects[key] = body;
            if (_storeThenFail.Keys.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
                return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
        if (request.Method == HttpMethod.Delete)
        {
            if (_failDeletes.ContainsKey(key)) return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            _objects.TryRemove(key, out _);
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
        if (!_objects.TryGetValue(key, out var bytes)) return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        var content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : bytes);
        content.Headers.ContentLength = bytes.Length;
        if (query["response-content-disposition"] is { } disposition)
            content.Headers.ContentDisposition = ContentDispositionHeaderValue.Parse(disposition);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
    }
}

