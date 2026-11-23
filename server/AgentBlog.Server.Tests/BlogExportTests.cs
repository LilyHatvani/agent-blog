using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AgentBlog.Announce;
using AgentBlog.Export;
using AgentBlog.Notify;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Export everything and import it elsewhere (docs/specs/export.md, ROADMAP M5): a blog with posts, revisions, a picture,
/// a writer, an agent's request, a note and a subscriber moves to another instance whole, ids and addresses kept, its
/// history marked imported so nothing is announced or sent again; no sign-in, token or secret travels; and the plan
/// covers the schema exactly.
/// </summary>
public sealed class BlogExportTests(BlogApp app)
{
    private static readonly StubAccount Anna = new("600000000000000001", "Anna", null);

    [Fact]
    public async Task The_export_plan_decides_about_every_table_and_column_of_the_schema()
    {
        await using var conn = await app.OpenAsOwnerAsync();
        Assert.Empty(await BlogExport.Plan.UndecidedAsync(conn));
    }

    private sealed record Source(SubscribeTests.Mailing M, string Blog, string OwnerId, string LivePost, string LiveSlug, string MediaId,
        string MediaPath, byte[] Zip);

    private static Action<IWebHostBuilder> Pictures(FakeWeb? web = null) => MediaHost.Configure(new FakeBucket(), web);

    /// <summary>A blog with something of everything, exported.</summary>
    private static async Task<Source> SourceAsync()
    {
        var web = new FakeWeb();
        web.Serve("https://pics.example/cat.png", MediaHost.Png, "image/png");
        var m = await SubscribeTests.Mailing.StartAsync(also: Pictures(web));
        var w = m.W;
        var ownerId = (await w.Owner.GetJsonAsync("/api/me")).Json!["account"]!["id"]!.GetValue<string>();

        var live = await m.OwnerPublishesAsync("Moving day");
        var liveSlug = (await w.OkAsync("read_post", new { post = live }))["slug"]!.GetValue<string>();
        var media = (await w.OkAsync("upload_media_from_url", new { url = "https://pics.example/cat.png", alt = "A cat" }))["media"]!;
        var draft = await w.DraftAsync("With a picture", $"Look: {media["markdown"]!.GetValue<string>()}");
        await w.OkAsync("publish_post", new { post = draft["id"]!.GetValue<string>() }); // a request waiting
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostJsonAsync($"/api/blogs/{m.Blog}/notes", new { note = "Keep it short." }, csrf: true)).Status);

        var link = (await w.Owner.PostJsonAsync($"/api/blogs/{m.Blog}/invitations", new { role = "writer" }, csrf: true)).Json!["link"]!.GetValue<string>();
        var anna = w.Host.Browser();
        var begin = await anna.PostJsonAsync("/api/auth/invite", new { token = link[(link.LastIndexOf('/') + 1)..], provider = "discord" });
        await anna.CompleteAtProviderAsync(new Uri(begin.Json!["url"]!.GetValue<string>()), Anna);
        await m.AddEmailAsync(); // subscribing opens once the blog sends email
        await m.ConfirmedAsync("reader@example.com");

        var export = await w.Owner.Http.GetAsync($"/api/blogs/{m.Blog}/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/zip", export.Content.Headers.ContentType!.MediaType);
        Assert.Contains(".agent-blog.zip", export.Content.Headers.ContentDisposition!.ToString());
        return new Source(m, m.Blog, ownerId, live, liveSlug, media["id"]!.GetValue<string>(), media["path"]?.GetValue<string>()
            ?? $"/media/{media["id"]!.GetValue<string>()}/cat.png", await export.Content.ReadAsByteArrayAsync());
    }

    private static async Task<JsonResult> ImportAsync(SignInBrowser owner, string blog, byte[] zip)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/blogs/{blog}/import-export") { Content = new ByteArrayContent(zip) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        request.Headers.Add("X-Blog-Request", "1");
        var r = await owner.Http.SendAsync(request);
        var text = await r.Content.ReadAsStringAsync();
        return new JsonResult(r.StatusCode, text.TrimStart().StartsWith('{') ? JsonNode.Parse(text)!.AsObject() : null);
    }

    private static string Document(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));
        using var reader = new StreamReader(archive.GetEntry("export.json")!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task A_blog_moves_whole_to_another_instance_and_nothing_is_announced_or_sent_again()
    {
        // The new home, set up before the old blog's last posts: its owner already announces by email, gets notified on
        // Discord and pings search engines, so a re-announcement, a notification or a ping about old news would all show
        // (none of them can hide behind "older than the channel").
        var search = new StubSearch();
        await using var dst = await SubscribeTests.Mailing.StartAsync(also: b =>
        {
            Pictures()(b);
            b.UseSetting("Blog:IndexNow:Endpoint", "https://indexnow.test/indexnow");
            b.ConfigureTestServices(s => s.AddHttpClient(AgentBlog.Public.SearchPing.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => search));
        });
        await dst.AddEmailAsync();
        Assert.Equal(HttpStatusCode.OK, (await dst.W.Owner.PostJsonAsync($"/api/blogs/{dst.Blog}/channels",
            new { kind = "discord", label = "Mine", url = "https://discord.com/api/webhooks/123456/secret-token-abcd" }, csrf: true)).Status);
        await Task.Delay(20);
        var src = await SourceAsync();
        await using var _src = src.M;
        var srcAgent = (await src.M.W.Owner.GetJsonAsync("/api/connections")).Json!["connections"]![0]!["id"]!.GetValue<string>();
        var imported = await ImportAsync(dst.W.Owner, dst.Blog, src.Zip);
        Assert.True(imported.Status == HttpStatusCode.OK, imported.Json?.ToJsonString());
        Assert.Equal(1, imported.Json!["pictures"]!.GetValue<int>());
        Assert.Equal(1, imported.Json["subscribers"]!.GetValue<int>());

        // Posts with their ids and addresses, the live one served with its picture's address unchanged.
        var posts = (await dst.W.Owner.GetJsonAsync($"/api/blogs/{dst.Blog}/posts")).Json!["posts"]!.AsArray();
        Assert.Equal(2, posts.Count);
        Assert.Contains(posts, p => p!["id"]!.GetValue<string>() == src.LivePost);
        var page = await dst.Reader().GetAsync($"/posts/{src.LiveSlug}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Moving day", await page.Content.ReadAsStringAsync());
        var picture = await dst.Reader().GetAsync(src.MediaPath);
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal(MediaHost.Png, await picture.Content.ReadAsByteArrayAsync());

        // People, notes and the waiting request came; the writer is an account waiting to be claimed.
        Assert.Contains("Anna", (await dst.W.Owner.GetJsonAsync($"/api/blogs/{dst.Blog}/people")).Json!.ToJsonString());
        Assert.Contains("Keep it short.", (await dst.W.Owner.GetJsonAsync($"/api/blogs/{dst.Blog}/notes")).Json!.ToJsonString());
        Assert.Single((await dst.W.Owner.GetJsonAsync("/api/requests")).Json!["requests"]!.AsArray());
        Assert.Contains("reader@example.com", (await dst.W.Owner.GetJsonAsync($"/api/blogs/{dst.Blog}/subscribers")).Json!.ToJsonString());

        await using var conn = new NpgsqlConnection(dst.W.Host.App.AppConnectionString);
        // The exported owner is this owner now, everywhere: no row names the old one.
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM post_revision WHERE actor = @a", new { a = "account:" + src.OwnerId }));
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM event WHERE actor = @a", new { a = "account:" + src.OwnerId }));
        // The agent's connection is history here: revoked, no grant.
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM connection WHERE id = @id AND blog_id = @b AND revoked_at IS NOT NULL AND oauth_authorization_id IS NULL",
            new { id = Guid.Parse(srcAgent), b = Guid.Parse(dst.Blog) }));
        Assert.True(await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM event WHERE blog_id = @b AND imported", new { b = Guid.Parse(dst.Blog) }) > 5);

        // Every worker walks past the imported history: nothing announced, notified or mailed.
        await dst.Service<Announcer>().CreatePassAsync(default);
        await dst.Service<Notifier>().FanOutAsync(default);
        await dst.MailPassAsync();
        await dst.Service<AgentBlog.Public.SearchPing>().RunOnceAsync(default);
        Assert.Empty(search.Hits);
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM announcement WHERE blog_id = @b", new { b = Guid.Parse(dst.Blog) }));
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM mail_out WHERE blog_id = @b", new { b = Guid.Parse(dst.Blog) }));
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM delivery WHERE blog_id = @b", new { b = Guid.Parse(dst.Blog) }));

        // Something new on the new home is announced as ever.
        await dst.OwnerPublishesAsync("First post here");
        await dst.Service<Announcer>().CreatePassAsync(default);
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM announcement WHERE blog_id = @b", new { b = Guid.Parse(dst.Blog) }));

        // Into the same blog twice: refused, it isn't empty now.
        var again = await ImportAsync(dst.W.Owner, dst.Blog, src.Zip);
        Assert.Equal(HttpStatusCode.BadRequest, again.Status);
        Assert.Contains("nothing in it yet", again.Json!["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task No_sign_in_token_or_secret_travels_and_only_the_owner_exports_or_imports()
    {
        var src = await SourceAsync();
        await using var _src = src.M;
        var doc = Document(src.Zip);
        Assert.StartsWith("{\"format\":\"agent-blog-export/1\"", doc);
        foreach (var absent in new[] { "account_identity", "oauth_authorization_id", "passkey_handle", "account_session", "channel_secret",
                     "blog_secret", "200000000000000001", Anna.Id, src.M.W.Agent.AccessToken, "email_ciphertext", "token_hash" })
            Assert.DoesNotContain(absent, doc);

        // A writer can't take the blog away, or bring one in.
        var writer = src.M.W.Host.Browser();
        var link = (await src.M.W.Owner.PostJsonAsync($"/api/blogs/{src.Blog}/invitations", new { role = "editor" }, csrf: true)).Json!["link"]!.GetValue<string>();
        var begin = await writer.PostJsonAsync("/api/auth/invite", new { token = link[(link.LastIndexOf('/') + 1)..], provider = "discord" });
        await writer.CompleteAtProviderAsync(new Uri(begin.Json!["url"]!.GetValue<string>()), new StubAccount("600000000000000002", "Ben", null));
        Assert.Equal(HttpStatusCode.NotFound, (await writer.Http.GetAsync($"/api/blogs/{src.Blog}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ImportAsync(writer, src.Blog, src.Zip)).Status);
    }

    [Fact]
    public async Task An_import_says_what_s_wrong_before_it_changes_anything()
    {
        var src = await SourceAsync();
        await using var _src = src.M;

        // A home without a master key can't keep the subscribers' addresses sealed: it says so and imports nothing.
        await using var bare = await PostTests.World.StartAsync(Pictures());
        var blog = (await bare.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
        var refused = await ImportAsync(bare.Owner, blog, src.Zip);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("Blog__Secrets__MasterKey", refused.Json!["detail"]!.GetValue<string>());
        Assert.Empty((await bare.Owner.GetJsonAsync($"/api/blogs/{blog}/posts")).Json!["posts"]!.AsArray());

        Assert.Equal(HttpStatusCode.BadRequest, (await ImportAsync(bare.Owner, blog, "not a zip"u8.ToArray())).Status);
        using var other = new MemoryStream();
        using (var z = new ZipArchive(other, ZipArchiveMode.Create, leaveOpen: true))
        await using (var e = z.CreateEntry("export.json").Open())
            await e.WriteAsync("{\"format\":\"relay-export/1\",\"counts\":{},\"tables\":{}}"u8.ToArray());
        var wrong = await ImportAsync(bare.Owner, blog, other.ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);
        Assert.Contains("relay-export/1", wrong.Json!["detail"]!.GetValue<string>());
    }
}
