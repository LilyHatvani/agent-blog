using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentBlog.Media;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Media (docs/specs/media.md) through the real host, an agent connected the way claude.ai connects and a bucket in
/// memory: the PUT-then-confirm upload and what confirm checks, the fetch from a URL and what it refuses, what readers
/// get at /media/…, the owner's trash, the sweep of slots nobody confirmed, and the blog's quota.
/// </summary>
public sealed class MediaTests
{
    private sealed class MediaWorld : IAsyncDisposable
    {
        public required PostTests.World World { get; init; }
        public required FakeBucket Bucket { get; init; }
        public required FakeWeb Web { get; init; }

        public static async Task<MediaWorld> StartAsync(long maxBlogBytes = 1024 * 1024, bool realFetch = false)
        {
            var bucket = new FakeBucket();
            var web = new FakeWeb();
            var world = await PostTests.World.StartAsync(MediaHost.Configure(bucket, realFetch ? null : web, maxBlogBytes));
            return new MediaWorld { World = world, Bucket = bucket, Web = web };
        }

        public Task<(bool Refused, JsonObject Body)> ToolAsync(string tool, object? args = null) => World.ToolAsync(tool, args);

        public Task<JsonObject> OkAsync(string tool, object? args = null) => World.OkAsync(tool, args);

        public Task<JsonObject> SlotAsync(byte[] bytes, string type = "image/png", string? filename = "Harbour at Dawn.png", string? alt = "The harbour") =>
            OkAsync("upload_media", new { content_type = type, size_bytes = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), alt, filename });

        /// <summary>The agent's PUT, exactly as the slot says.</summary>
        public async Task<HttpStatusCode> PutAsync(JsonObject slot, byte[] bytes)
        {
            var upload = slot["upload"]!;
            using var req = new HttpRequestMessage(HttpMethod.Put, upload["url"]!.GetValue<string>()) { Content = new ByteArrayContent(bytes) };
            foreach (var (name, value) in upload["headers"]!.AsObject())
            {
                var v = value!.GetValue<string>();
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) req.Content.Headers.ContentType = new(v);
                else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) req.Content.Headers.ContentLength = long.Parse(v);
                else req.Headers.TryAddWithoutValidation(name, v);
            }
            using var client = Bucket.Client();
            return (await client.SendAsync(req)).StatusCode;
        }

        public async Task<JsonObject> UploadAsync(byte[] bytes, string type = "image/png", string? filename = "Harbour at Dawn.png")
        {
            var slot = await SlotAsync(bytes, type, filename);
            Assert.Equal(HttpStatusCode.OK, await PutAsync(slot, bytes));
            return (await OkAsync("confirm_media", new { media_id = MediaId(slot) }))["media"]!.AsObject();
        }

        public Task<HttpResponseMessage> ReaderGetAsync(string path, string? ifNoneMatch = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, path);
            if (ifNoneMatch is not null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            return World.Host.Browser().Http.SendAsync(req);
        }

        public MediaService Service => World.Host.App.Factory.Services.GetRequiredService<MediaService>();

        public async Task<string> StatusAsync(string id)
        {
            await using var conn = new NpgsqlConnection(World.Host.App.AppConnectionString);
            return await conn.QuerySingleAsync<string>("SELECT status FROM media WHERE id = @id", new { id = Guid.Parse(id) });
        }

        public string Key(string id) => $"blogs/{BlogId}/media/{id}";

        private string? _blog;
        public string BlogId => _blog ??= BlogIdAsync().GetAwaiter().GetResult();

        private async Task<string> BlogIdAsync()
        {
            await using var conn = new NpgsqlConnection(World.Host.App.AppConnectionString);
            return (await conn.QuerySingleAsync<Guid>("SELECT id FROM blog")).ToString();
        }

        public ValueTask DisposeAsync() => World.DisposeAsync();
    }

    private static string MediaId(JsonObject slot) => slot["media"]!["id"]!.GetValue<string>();

    // ------------------------------------------------------------------------------------------ upload by PUT

    [Fact]
    public async Task An_agent_at_Propose_uploads_a_picture_and_readers_get_it_from_the_blogs_own_address()
    {
        await using var w = await MediaWorld.StartAsync();

        var slot = await w.SlotAsync(MediaHost.Png);
        Assert.Equal("pending", slot["media"]!["status"]!.GetValue<string>());
        Assert.StartsWith(MediaHost.BucketEndpoint + "/" + MediaHost.BucketName + "/blogs/", slot["upload"]!["url"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, await w.PutAsync(slot, MediaHost.Png));

        var media = (await w.OkAsync("confirm_media", new { media_id = MediaId(slot) }))["media"]!;
        Assert.Equal("ready", media["status"]!.GetValue<string>());
        var path = media["path"]!.GetValue<string>();
        Assert.Equal($"/media/{MediaId(slot)}/harbour-at-dawn.png", path);
        Assert.Equal($"![The harbour]({path})", media["markdown"]!.GetValue<string>());
        Assert.Equal(SignInHost.PublicUrl + path, media["url"]!.GetValue<string>());

        var served = await w.ReaderGetAsync(path);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType!.MediaType);
        Assert.Equal(MediaHost.Png, await served.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());
        var etag = served.Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.NotModified, (await w.ReaderGetAsync(path, etag)).StatusCode);

        // One address per picture; the right id under another name is not it.
        Assert.Equal(HttpStatusCode.NotFound, (await w.ReaderGetAsync($"/media/{MediaId(slot)}/other.png")).StatusCode);

        // Confirming again changes nothing; listing shows it.
        Assert.Equal("ready", (await w.OkAsync("confirm_media", new { media_id = MediaId(slot) }))["media"]!["status"]!.GetValue<string>());
        Assert.Single((await w.OkAsync("list_media"))["media"]!.AsArray());

        var events = await w.World.EventsAsync();
        Assert.Contains(("media_upload_started", $"connection:{w.World.Agent.Id}"), events);
        Assert.Contains(("media_added", $"connection:{w.World.Agent.Id}"), events);
    }

    [Fact]
    public async Task A_picture_not_yet_confirmed_is_not_served()
    {
        await using var w = await MediaWorld.StartAsync();
        var slot = await w.SlotAsync(MediaHost.Png);
        await w.PutAsync(slot, MediaHost.Png);
        Assert.Equal(HttpStatusCode.NotFound,
            (await w.ReaderGetAsync($"/media/{MediaId(slot)}/harbour-at-dawn.png")).StatusCode);
    }

    [Fact]
    public async Task Confirm_reads_the_bytes_back_and_refuses_what_was_not_declared()
    {
        await using var w = await MediaWorld.StartAsync();

        // Nothing PUT yet.
        var slot = await w.SlotAsync(MediaHost.Png);
        var (refused, body) = await w.ToolAsync("confirm_media", new { media_id = MediaId(slot) });
        Assert.True(refused);
        Assert.Equal("not_uploaded", body["code"]!.GetValue<string>());

        // A bucket that ignores the signed checksum took other bytes: confirm catches it and removes them.
        var url = slot["upload"]!["url"]!.GetValue<string>();
        w.Bucket.StoreIgnoringChecks(url, MediaHost.Gif);
        (refused, body) = await w.ToolAsync("confirm_media", new { media_id = MediaId(slot) });
        Assert.True(refused);
        Assert.Equal("upload_mismatch", body["code"]!.GetValue<string>());
        Assert.False(w.Bucket.Holds(url));
        Assert.Equal("pending", await w.StatusAsync(MediaId(slot)));

        // The right bytes to the same slot, before it expires, then work.
        Assert.Equal(HttpStatusCode.OK, await w.PutAsync(slot, MediaHost.Png));
        Assert.Equal("ready", (await w.OkAsync("confirm_media", new { media_id = MediaId(slot) }))["media"]!["status"]!.GetValue<string>());

        // Bytes whose hash matches but which are another type than declared.
        var jpegAsPng = await w.SlotAsync(MediaHost.Jpeg, type: "image/png");
        await w.PutAsync(jpegAsPng, MediaHost.Jpeg);
        (refused, body) = await w.ToolAsync("confirm_media", new { media_id = MediaId(jpegAsPng) });
        Assert.True(refused);
        Assert.Equal("upload_mismatch", body["code"]!.GetValue<string>());
        Assert.Contains("image/jpeg", body["message"]!.GetValue<string>());

        // Text with a picture's name is no picture.
        var text = "<svg onload=alert(1)>"u8.ToArray();
        var fake = await w.SlotAsync(text, type: "image/gif");
        await w.PutAsync(fake, text);
        (refused, body) = await w.ToolAsync("confirm_media", new { media_id = MediaId(fake) });
        Assert.True(refused);
        Assert.Contains("isn't a picture", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_upload_slot_says_why_it_is_refused()
    {
        await using var w = await MediaWorld.StartAsync();
        async Task<string> Code(object args) => (await w.ToolAsync("upload_media", args)).Body["code"]!.GetValue<string>();
        var sha = Convert.ToHexStringLower(SHA256.HashData(MediaHost.Png));

        Assert.Equal("unsupported_type", await Code(new { content_type = "image/svg+xml", size_bytes = 10, sha256 = sha }));
        Assert.Equal("too_large", await Code(new { content_type = "image/png", size_bytes = MediaHost.MaxBytes + 1, sha256 = sha }));
        Assert.Equal("too_large", await Code(new { content_type = "image/png", size_bytes = 0, sha256 = sha }));
        Assert.Equal("invalid_argument", await Code(new { content_type = "image/png", size_bytes = 10, sha256 = "abc" }));
        Assert.Equal("invalid_argument", await Code(new { content_type = "image/png", size_bytes = 10, sha256 = sha, alt = new string('a', 501) }));
        // base64 works as well as hex
        Assert.False((await w.ToolAsync("upload_media", new { content_type = "image/png", size_bytes = MediaHost.Png.Length,
            sha256 = Convert.ToBase64String(SHA256.HashData(MediaHost.Png)) })).Refused);
    }

    [Fact]
    public async Task Another_connection_cannot_confirm_a_slot_it_did_not_ask_for()
    {
        await using var w = await MediaWorld.StartAsync();
        var slot = await w.SlotAsync(MediaHost.Png);
        await w.PutAsync(slot, MediaHost.Png);
        var other = await ConnectionTests.ConnectAsync(w.World.Owner, "Other agent");
        var result = await ConnectionTests.RpcResultAsync(await ConnectionTests.McpAsync(w.World.Host, other.AccessToken, "tools/call",
            new { name = "confirm_media", arguments = new { media_id = MediaId(slot) } }));
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("not_found", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("pending", await w.StatusAsync(MediaId(slot)));
    }

    // ------------------------------------------------------------------------------------------- from a URL

    [Fact]
    public async Task From_a_URL_the_server_keeps_a_copy_of_a_picture_and_refuses_what_is_not_one()
    {
        await using var w = await MediaWorld.StartAsync();
        w.Web.Serve("https://pics.example/animals/Cat%20Nap.gif", MediaHost.Gif, "image/gif");
        w.Web.Serve("https://pics.example/page", "<html>hi</html>"u8.ToArray(), "image/png"); // lies about its type
        w.Web.Serve("https://pics.example/big.png", [.. MediaHost.Png, .. new byte[MediaHost.MaxBytes]], "image/png");

        var media = (await w.OkAsync("upload_media_from_url", new { url = "https://pics.example/animals/Cat%20Nap.gif", alt = "A cat" }))["media"]!;
        Assert.Equal("ready", media["status"]!.GetValue<string>());
        Assert.Equal("cat-nap.gif", media["filename"]!.GetValue<string>());
        Assert.Equal("image/gif", media["content_type"]!.GetValue<string>());
        Assert.Equal("https://pics.example/animals/Cat%20Nap.gif", media["source_url"]!.GetValue<string>());
        Assert.Equal(MediaHost.Gif, await (await w.ReaderGetAsync(media["path"]!.GetValue<string>())).Content.ReadAsByteArrayAsync());

        async Task<string> Code(string url) => (await w.ToolAsync("upload_media_from_url", new { url })).Body["code"]!.GetValue<string>();
        Assert.Equal("not_a_picture", await Code("https://pics.example/page"));
        Assert.Equal("too_large", await Code("https://pics.example/big.png"));
        Assert.Equal("fetch_failed", await Code("https://pics.example/missing.png"));
        Assert.Equal("invalid_argument", await Code("ftp://pics.example/cat.gif"));
        Assert.Equal("invalid_argument", await Code("https://user:pass@pics.example/cat.gif"));
        Assert.Equal("invalid_argument", await Code("/media/relative.png"));
    }

    [Theory]
    [InlineData("http://127.0.0.1/x.png")]
    [InlineData("http://localhost/x.png")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::1]/x.png")]
    [InlineData("http://10.1.2.3/x.png")]
    [InlineData("http://100.64.0.1/x.png")]
    [InlineData("http://192.168.1.1/x.png")]
    [InlineData("http://[::ffff:127.0.0.1]/x.png")]
    [InlineData("http://0.0.0.0/x.png")]
    [InlineData("https://8.8.8.8:8443/x.png")]
    public async Task The_real_fetch_never_connects_inside_the_network(string url)
    {
        // No stub: the server's own guarded client, the one production runs. The refusal comes before any connection.
        await using var w = await MediaWorld.StartAsync(realFetch: true);
        var (refused, body) = await w.ToolAsync("upload_media_from_url", new { url });
        Assert.True(refused);
        Assert.Equal("address_not_allowed", body["code"]!.GetValue<string>());
    }

    // ----------------------------------------------------------------------------------------- the owner's

    [Fact]
    public async Task The_owner_trashes_a_picture_readers_stop_getting_it_and_restore_brings_it_back()
    {
        await using var w = await MediaWorld.StartAsync();
        var media = await w.UploadAsync(MediaHost.Png);
        var id = media["id"]!.GetValue<string>();
        var path = media["path"]!.GetValue<string>();

        var library = await w.World.Owner.GetJsonAsync($"/api/blogs/{w.BlogId}/media");
        Assert.True(library.Json!["enabled"]!.GetValue<bool>());
        Assert.Equal("Claude", library.Json["media"]![0]!["by_label"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.OK, (await w.World.OwnerPostAsync($"/api/media/{id}/trash")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await w.ReaderGetAsync(path)).StatusCode);
        Assert.True(w.Bucket.Holds($"{MediaHost.BucketEndpoint}/{MediaHost.BucketName}/{w.Key(id)}")); // the bytes stay
        Assert.Equal("trash", (await w.World.Owner.GetJsonAsync($"/api/blogs/{w.BlogId}/media?status=trash")).Json!["media"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await w.World.OwnerPostAsync($"/api/media/{id}/trash")).Status);

        Assert.Equal(HttpStatusCode.OK, (await w.World.OwnerPostAsync($"/api/media/{id}/restore")).Status);
        Assert.Equal(HttpStatusCode.OK, (await w.ReaderGetAsync(path)).StatusCode);

        // No agent tool trashes media yet, at any level.
        await w.World.SetLevelAsync("full");
        Assert.DoesNotContain("trash_media", await ConnectionTests.ListToolsAsync(w.World.Host, w.World.Agent.AccessToken));

        // Another account's request reads as not found, and a write needs the CSRF header.
        Assert.Equal(HttpStatusCode.Forbidden, (await w.World.Owner.PostJsonAsync($"/api/media/{id}/trash", null, csrf: false)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await w.World.OwnerPostAsync($"/api/media/{Guid.NewGuid()}/trash")).Status);
    }

    [Fact]
    public async Task A_slot_nobody_confirms_expires_and_its_bytes_are_removed()
    {
        await using var w = await MediaWorld.StartAsync();
        var slot = await w.SlotAsync(MediaHost.Png);
        await w.PutAsync(slot, MediaHost.Png);
        var url = slot["upload"]!["url"]!.GetValue<string>();
        Assert.True(w.Bucket.Holds(url));

        Assert.Equal(0, await w.Service.SweepAsync(DateTimeOffset.UtcNow, CancellationToken.None)); // not due yet
        Assert.Equal(1, await w.Service.SweepAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None));
        Assert.Equal("expired", await w.StatusAsync(MediaId(slot)));
        Assert.False(w.Bucket.Holds(url));
        Assert.Contains(("media_upload_expired", "system"), await w.World.EventsAsync());

        var (refused, body) = await w.ToolAsync("confirm_media", new { media_id = MediaId(slot) });
        Assert.True(refused);
        Assert.Equal("expired", body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_blog_over_its_quota_is_told_how_full_it_is()
    {
        await using var w = await MediaWorld.StartAsync(maxBlogBytes: 6000);
        var big = new byte[4000];
        MediaHost.Png.CopyTo(big, 0);
        await w.SlotAsync(big); // pending counts: its bytes may land
        var (refused, body) = await w.ToolAsync("upload_media", new
        {
            content_type = "image/png", size_bytes = big.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(big)),
        });
        Assert.True(refused);
        Assert.Equal("blog_full", body["code"]!.GetValue<string>());
        Assert.Contains("4000 of its 6000", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_a_bucket_media_says_so()
    {
        await using var w = await PostTests.World.StartAsync();
        var (refused, body) = await w.ToolAsync("upload_media", new
        {
            content_type = "image/png", size_bytes = 10, sha256 = new string('a', 64),
        });
        Assert.True(refused);
        Assert.Equal("media_off", body["code"]!.GetValue<string>());
        Assert.Contains("Blog__Bucket__", body["message"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------------------------- in posts and feeds

    [Fact]
    public async Task A_picture_in_a_live_post_shows_on_its_page_and_has_an_absolute_address_in_the_feeds()
    {
        await using var w = await MediaWorld.StartAsync();
        var media = await w.UploadAsync(MediaHost.Png);
        var post = await w.World.DraftAsync("With a picture", "Look:\n\n" + media["markdown"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await w.World.OwnerPostAsync($"/api/posts/{post["id"]}/publish")).Status);

        var path = media["path"]!.GetValue<string>();
        var page = await (await w.ReaderGetAsync("/posts/with-a-picture")).Content.ReadAsStringAsync();
        Assert.Contains($"src=\"{path}\"", page);
        Assert.Contains($"src=\"{SignInHost.PublicUrl}{path}\"", await (await w.ReaderGetAsync("/feed.xml")).Content.ReadAsStringAsync());
        // Atom carries the HTML escaped as text: &lt;img src="…"
        Assert.Contains($"&lt;img src=\"{SignInHost.PublicUrl}{path}\"", await (await w.ReaderGetAsync("/atom.xml")).Content.ReadAsStringAsync());
    }
}
