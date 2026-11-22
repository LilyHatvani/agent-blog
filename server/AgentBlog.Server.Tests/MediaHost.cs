using System.Collections.Concurrent;
using System.Net;
using AgentBlog.Media;
using AgentBlog.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AgentBlog.Server.Tests;

/// <summary>The settings and stubs a media test host runs with: a bucket in memory, and (optionally) a web of fixed answers.</summary>
internal static class MediaHost
{
    public const string BucketEndpoint = "https://bucket.test";
    public const string BucketName = "blog-test";
    public const long MaxBytes = 4096;

    /// <param name="web">The outside web as the fetch sees it; null leaves the real, guarded client in place.</param>
    public static Action<IWebHostBuilder> Configure(FakeBucket bucket, FakeWeb? web, long maxBlogBytes = 1024 * 1024) => b =>
    {
        b.UseSetting("Blog:Bucket:endpoint", BucketEndpoint);
        b.UseSetting("Blog:Bucket:name", BucketName);
        b.UseSetting("Blog:Bucket:access_key", "test-access");
        b.UseSetting("Blog:Bucket:secret_key", "test-secret");
        b.UseSetting("Blog:Bucket:max_bytes", MaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        b.UseSetting("Blog:Bucket:max_blog_bytes", maxBlogBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        b.ConfigureTestServices(s =>
        {
            s.AddHttpClient(Bucket.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => bucket);
            if (web is not null) s.AddHttpClient(SafeFetch.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => web);
        });
    };

    // The smallest valid files of each type, for the bytes a check reads.
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    public static readonly byte[] Gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0xFF, 0xD9];
}

/// <summary>The outside web as the server's picture fetch sees it: a fixed table of answers, 404 otherwise.</summary>
internal sealed class FakeWeb : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, (HttpStatusCode Status, byte[] Body, string Type)> _pages = new();

    public ConcurrentBag<string> Fetched { get; } = [];

    public void Serve(string url, byte[] body, string type, HttpStatusCode status = HttpStatusCode.OK) => _pages[url] = (status, body, type);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Fetched.Add(request.RequestUri!.AbsoluteUri);
        if (!_pages.TryGetValue(request.RequestUri!.AbsoluteUri, out var page)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        var content = new ByteArrayContent(page.Body);
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(page.Type);
        return Task.FromResult(new HttpResponseMessage(page.Status) { Content = content });
    }
}
