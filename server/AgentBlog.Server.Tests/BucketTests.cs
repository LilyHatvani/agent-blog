using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgentBlog.Storage;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// The bucket client's answers when the bucket says no (docs/specs/import.md § Pictures): the reason the bucket gave — its
/// status, S3 error code and message — reaches the caller, with the shape of the request, and nothing that names the
/// credentials or where the bucket is. And a normal write still sends exactly the headers its URL signs. No database.
/// </summary>
public sealed class BucketTests
{
    private const string AccessKey = "AKIAREALLOOKINGKEY01";
    private const string SecretKey = "wJalrXUtnFEMIsecretSECRETsecret0000000000";

    /// <summary>A bucket that answers every request with one fixed response, and remembers what it was sent.</summary>
    private sealed class OneAnswer(HttpStatusCode status, string? body, string type = "application/xml") : HttpMessageHandler
    {
        public HttpRequestMessage? Sent { get; private set; }
        public byte[]? SentBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent = request;
            if (request.Content is not null) SentBody = await request.Content.ReadAsByteArrayAsync(ct);
            var r = new HttpResponseMessage(status);
            if (body is not null) r.Content = new StringContent(body, Encoding.UTF8, type);
            return r;
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static Bucket BucketAt(HttpMessageHandler handler, BucketUrlStyle style = BucketUrlStyle.Path, string name = "blog-media") => new(new BucketSettings
    {
        Endpoint = new Uri("https://acct123.r2.cloudflarestorage.com"),
        Name = name,
        AccessKey = AccessKey,
        SecretKey = SecretKey,
        Region = "auto",
        UrlStyle = style,
    }, new Clients(handler), TimeProvider.System);

    private static readonly byte[] Bytes = MediaHost.Png;

    /// <summary>An S3 error body with every element a real one may carry, the ones that must never reach a log included.</summary>
    private static string ErrorBody(string code, string message) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Error><Code>{code}</Code><Message>{message}</Message>
        <AWSAccessKeyId>{AccessKey}</AWSAccessKeyId>
        <StringToSign>AWS4-HMAC-SHA256
        20261125T120000Z
        20261125/auto/s3/aws4_request</StringToSign>
        <CanonicalRequest>PUT /blog-media/blogs/x?X-Amz-Credential={AccessKey}%2F20261125 host:acct123.r2.cloudflarestorage.com</CanonicalRequest>
        <SignatureProvided>deadbeef</SignatureProvided><RequestId>r-1</RequestId></Error>
        """;

    [Fact]
    public async Task A_refused_PUT_says_the_buckets_code_and_message_and_the_requests_shape()
    {
        var handler = new OneAnswer(HttpStatusCode.BadRequest, ErrorBody("InvalidArgument", "Invalid Argument: x-amz-checksum-sha256"));
        var e = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            BucketAt(handler).WriteAsync("blogs/b/media/m", "image/webp", Bytes, SHA256.HashData(Bytes), default));

        Assert.Equal(HttpStatusCode.BadRequest, e.StatusCode);
        Assert.Contains("400", e.Message);
        Assert.Contains("InvalidArgument", e.Message);
        Assert.Contains("Invalid Argument: x-amz-checksum-sha256", e.Message);
        // The shape of the request, so the next run says which part the bucket didn't take.
        Assert.Contains("content-length;content-type;host;x-amz-checksum-sha256", e.Message);
        Assert.Contains("region auto", e.Message);
        Assert.Contains("path-style", e.Message);
        Assert.Contains("image/webp", e.Message);
        Assert.Contains($"{Bytes.Length} bytes", e.Message);
    }

    [Fact]
    public async Task A_refused_PUT_never_carries_the_credentials_the_signature_or_where_the_bucket_is()
    {
        // A message that echoes the key, the host and a signed parameter, as a misbehaving S3-compatible store might.
        var handler = new OneAnswer(HttpStatusCode.Forbidden, ErrorBody("SignatureDoesNotMatch",
            $"Key {AccessKey} at acct123.r2.cloudflarestorage.com/blog-media/blogs/b/media/m with X-Amz-Signature=abc123, X-Amz-Credential%3Dxyz789 and secret {SecretKey} for " + new string('a', 64)));
        var e = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            BucketAt(handler).WriteAsync("blogs/b/media/m", "image/png", Bytes, SHA256.HashData(Bytes), default));

        Assert.Contains("SignatureDoesNotMatch", e.Message);
        Assert.DoesNotContain(AccessKey, e.Message);
        Assert.DoesNotContain(SecretKey, e.Message);
        Assert.DoesNotContain("acct123", e.Message);
        Assert.DoesNotContain("r2.cloudflarestorage.com", e.Message);
        Assert.DoesNotContain("abc123", e.Message);
        Assert.DoesNotContain("X-Amz-Signature", e.Message);
        Assert.DoesNotContain("X-Amz-Credential", e.Message);
        Assert.DoesNotContain("StringToSign", e.Message);
        Assert.DoesNotContain("AWS4-HMAC-SHA256", e.Message);
        Assert.DoesNotContain("deadbeef", e.Message);
        Assert.DoesNotContain("blogs/b/media/m", e.Message);
        Assert.DoesNotContain("blog-media", e.Message);
        Assert.DoesNotContain("xyz789", e.Message);
        Assert.DoesNotContain(new string('a', 64), e.Message);
    }

    [Fact]
    public async Task A_virtual_hosted_buckets_own_host_is_left_out_too()
    {
        var handler = new OneAnswer(HttpStatusCode.BadRequest, ErrorBody("InvalidRequest", "Bad host blog-media.acct123.r2.cloudflarestorage.com"));
        var e = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            BucketAt(handler, BucketUrlStyle.Virtual).WriteAsync("k", "image/png", Bytes, SHA256.HashData(Bytes), default));
        Assert.Contains("InvalidRequest", e.Message);
        Assert.Contains("virtual-hosted", e.Message);
        Assert.DoesNotContain("acct123", e.Message);
        Assert.DoesNotContain("blog-media", e.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    public async Task A_refusal_without_an_S3_error_says_so_and_still_names_the_status(string? body)
    {
        var handler = new OneAnswer(HttpStatusCode.BadGateway, body, "text/html");
        var e = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            BucketAt(handler).WriteAsync("k", "image/png", Bytes, SHA256.HashData(Bytes), default));
        Assert.Contains("502", e.Message);
        Assert.Contains("no S3 error", e.Message);
        Assert.DoesNotContain("<html>", e.Message);
    }

    [Fact]
    public async Task A_refused_GET_says_the_buckets_code_and_message_too()
    {
        var handler = new OneAnswer(HttpStatusCode.Forbidden, ErrorBody("AccessDenied", "Access Denied"));
        var e = await Assert.ThrowsAnyAsync<HttpRequestException>(() => BucketAt(handler).OpenAsync("k", default));
        Assert.Contains("AccessDenied: Access Denied", e.Message);
        Assert.DoesNotContain(AccessKey, e.Message);
    }

    [Fact]
    public async Task A_long_or_marked_up_bucket_message_is_cut_and_its_control_characters_go()
    {
        var handler = new OneAnswer(HttpStatusCode.BadRequest, ErrorBody("InvalidArgument", "line one&#10;line two " + new string('x', 2000)));
        var e = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            BucketAt(handler).WriteAsync("k", "image/png", Bytes, SHA256.HashData(Bytes), default));
        Assert.DoesNotContain("\n", e.Message);
        Assert.True(e.Message.Length < 800, e.Message);
    }

    /// <summary>
    /// The signing is unchanged by the error work: a server-side write sends exactly the headers its presigned URL signs,
    /// with the values signed, and the body whose SHA-256 it signed — what any S3 bucket checks.
    /// </summary>
    [Fact]
    public async Task A_normal_write_sends_exactly_the_headers_its_url_signs_with_the_signed_values()
    {
        var handler = new OneAnswer(HttpStatusCode.OK, null);
        var sha = SHA256.HashData(Bytes);
        await BucketAt(handler).WriteAsync("blogs/b/media/m", "image/png", Bytes, sha, default);

        var sent = handler.Sent!;
        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal("/blog-media/blogs/b/media/m", sent.RequestUri!.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(sent.RequestUri.Query);
        Assert.Equal("content-length;content-type;host;x-amz-checksum-sha256", query["X-Amz-SignedHeaders"]);
        Assert.Equal("image/png", sent.Content!.Headers.ContentType!.ToString());
        Assert.Equal(Bytes.Length, sent.Content.Headers.ContentLength);
        Assert.Equal(Convert.ToBase64String(sha), Assert.Single(sent.Headers.GetValues(Bucket.ChecksumHeader)));
        Assert.Equal(Bytes, handler.SentBody);
        Assert.Null(sent.Headers.TransferEncodingChunked);
    }

    [Fact]
    public async Task The_header_the_bucket_refused_is_named_not_blanked()
    {
        // Whatever R2 refuses, its name is the diagnosis: a bare header name is kept, only signed values go.
        var handler = new OneAnswer(HttpStatusCode.BadRequest, ErrorBody("InvalidArgument", "Invalid Argument: x-amz-checksum-sha256"));
        var e = await Assert.ThrowsAsync<BucketRefusedException>(() =>
            BucketAt(handler).WriteAsync("blogs/b/media/m", "image/webp", Bytes, SHA256.HashData(Bytes), default));
        Assert.Equal("InvalidArgument", e.Code);
        Assert.Equal("Invalid Argument: x-amz-checksum-sha256", e.BucketMessage);
        Assert.Equal("PUT", e.Method);
    }

    [Fact]
    public async Task A_credential_straddling_the_cut_is_blanked_before_the_message_is_cut()
    {
        foreach (var secret in new[] { AccessKey, SecretKey, "acct123.r2.cloudflarestorage.com" })
        {
            var handler = new OneAnswer(HttpStatusCode.Forbidden, ErrorBody("AccessDenied", new string('x', 290) + secret));
            var e = await Assert.ThrowsAsync<BucketRefusedException>(() =>
                BucketAt(handler).WriteAsync("k", "image/png", Bytes, SHA256.HashData(Bytes), default));
            Assert.DoesNotContain(secret[..8], e.Message);
        }
    }

    /// <summary>A refusal whose body never finishes arriving: headers, then nothing, for ever.</summary>
    private sealed class StalledRefusal : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StreamContent(new NeverEnding()) });

        private sealed class NeverEnding : Stream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task A_refusal_whose_body_stalls_still_answers_within_the_deadline()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<BucketRefusedException>(() => BucketAt(new StalledRefusal()).OpenAsync("k", default));
        Assert.True(watch.Elapsed < Bucket.RefusalBodyDeadline + TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Equal(HttpStatusCode.Forbidden, e.StatusCode);
        Assert.Contains("no S3 error", e.Message);
    }

    [Fact]
    public async Task A_refused_PUT_whose_body_stalls_still_answers_within_the_deadline()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<BucketRefusedException>(() =>
            BucketAt(new StalledRefusal()).WriteAsync("k", "image/png", Bytes, SHA256.HashData(Bytes), default));
        Assert.True(watch.Elapsed < Bucket.RefusalBodyDeadline + TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Equal("PUT", e.Method);
    }

    [Fact]
    public async Task A_bucket_named_like_part_of_a_header_never_blanks_the_header_it_refused()
    {
        var handler = new OneAnswer(HttpStatusCode.BadRequest, ErrorBody("InvalidArgument", "Invalid Argument: x-amz-checksum-sha256"));
        var e = await Assert.ThrowsAsync<BucketRefusedException>(() =>
            BucketAt(handler, name: "checksum").WriteAsync("blogs/b/media/m", "image/png", Bytes, SHA256.HashData(Bytes), default));
        Assert.Equal("Invalid Argument: x-amz-checksum-sha256", e.BucketMessage);
    }

    [Fact]
    public async Task A_long_message_is_cut_on_a_characters_boundary_never_inside_an_emoji()
    {
        var handler = new OneAnswer(HttpStatusCode.BadRequest, ErrorBody("InvalidArgument", new string('x', 299) + "\U0001F600 and more"));
        var e = await Assert.ThrowsAsync<BucketRefusedException>(() =>
            BucketAt(handler).WriteAsync("k", "image/png", Bytes, SHA256.HashData(Bytes), default));
        var said = e.BucketMessage!;
        for (var i = 0; i < said.Length; i++)
            if (char.IsHighSurrogate(said[i])) Assert.True(i + 1 < said.Length && char.IsLowSurrogate(said[i + 1]), "a lone high surrogate");
            else if (char.IsLowSurrogate(said[i])) Assert.True(i > 0 && char.IsHighSurrogate(said[i - 1]), "a lone low surrogate");
    }
}
