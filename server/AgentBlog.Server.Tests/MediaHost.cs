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
    public static Action<IWebHostBuilder> Configure(FakeBucket bucket, HttpMessageHandler? web, long maxBlogBytes = 1024 * 1024) => b =>
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

/// <summary>A server that answers headers at once, then drips the body: one byte, and never the rest. The body is a
/// stream the reader reads directly, as a real network response's is (not a buffer filled ahead of time).</summary>
internal sealed class DrippingWeb : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = new DripContent();
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("image/png");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class DripContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new DripStream());

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new DripStream());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException("The body is read as a stream, never serialized into a buffer.");

        protected override bool TryComputeLength(out long length)
        {
            length = 100;
            return true;
        }
    }

    private sealed class DripStream : Stream
    {
        private bool _first = true;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_first)
            {
                _first = false;
                buffer.Span[0] = 0x89;
                return 1;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_first)
            {
                _first = false;
                buffer[offset] = 0x89;
                return 1;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 100;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
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
