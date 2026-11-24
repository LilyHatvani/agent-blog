using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AgentBlog.Import;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>Echo of Dreams' public blog API as the import sees it: a fixed set of posts, and a picture resolver.</summary>
internal sealed class StubEod : HttpMessageHandler
{
    public const string Api = "https://eod.test";
    public readonly List<JsonObject> Posts = [];
    /// <summary>The source's categories, each with id, slug, name and parentId (the hierarchy the import reads).</summary>
    public readonly List<JsonObject> Categories = [];
    /// <summary>Picture refs a signed-out reader may see, to the address of their public variant.</summary>
    public readonly Dictionary<string, string> Pictures = [];
    public HttpStatusCode? ListFails { get; set; }
    /// <summary>When set, the categories call answers this status instead of the list.</summary>
    public HttpStatusCode? CategoriesFails { get; set; }
    /// <summary>When set, the list call answers a 302 to this address instead of a page.</summary>
    public string? ListRedirect { get; set; }
    public ConcurrentBag<string> Asked { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        Asked.Add(request.Method + " " + uri.PathAndQuery);
        if (uri.AbsolutePath == "/api/blog/posts")
        {
            if (ListRedirect is { } to) return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };
            if (ListFails is { } failing) return new HttpResponseMessage(failing);
            var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
            int page = int.Parse(q["page"]!), size = int.Parse(q["pageSize"]!);
            var items = Posts.Skip((page - 1) * size).Take(size)
                .Select(p => new JsonObject { ["uuid"] = p["uuid"]!.DeepClone(), ["slug"] = p["slug"]!.DeepClone(), ["title"] = p["title"]!.DeepClone(), ["publishedAt"] = p["publishedAt"]!.DeepClone() });
            return Json(new JsonObject { ["items"] = new JsonArray([.. items]), ["page"] = page, ["pageSize"] = size, ["total"] = Posts.Count });
        }
        if (uri.AbsolutePath == "/api/blog/categories")
        {
            if (CategoriesFails is { } failing) return new HttpResponseMessage(failing);
            return Json(new JsonArray([.. Categories.Select(c => c.DeepClone())]));
        }
        if (uri.AbsolutePath.StartsWith("/api/blog/posts/", StringComparison.Ordinal))
        {
            var slug = Uri.UnescapeDataString(uri.AbsolutePath["/api/blog/posts/".Length..]);
            return Posts.FirstOrDefault(p => p["slug"]!.GetValue<string>() == slug) is { } post ? Json(post) : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        if (uri.AbsolutePath == "/api/pictures/resolve" && request.Method == HttpMethod.Post)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var refs = body["trees"]![0]!["refs"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
            // EoD's own cap: more than 100 refs in one call is refused whole (PicturesResolveEndpoints' MaxRefs).
            if (refs.Count > 100)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":"too_many_refs"}""", Encoding.UTF8, "application/json"),
                };
            var admitted = refs.Where(Pictures.ContainsKey).Select(r => new JsonObject
            {
                ["tree"] = 0, ["ref"] = r, ["uuid"] = Guid.NewGuid(), ["rating"] = 0, ["moderationStatus"] = 1, ["thumbUrl"] = Pictures[r],
            });
            return Json(new JsonObject { ["admission"] = "protected", ["pictures"] = new JsonArray([.. admitted]) });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(JsonNode body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    public static JsonObject Post(Guid uuid, string slug, string title, string content, string published, JsonObject? author,
        string[]? tags = null, JsonObject? category = null, string? cover = null, string? excerpt = null) => new()
    {
        ["uuid"] = uuid, ["slug"] = slug, ["title"] = title, ["excerpt"] = excerpt, ["content"] = content, ["coverImageUrl"] = cover,
        ["readingMinutes"] = 2, ["seoDescription"] = null, ["publishedAt"] = published, ["updatedAt"] = published,
        ["tags"] = new JsonArray([.. (tags ?? []).Select(t => (JsonNode)t)]), ["author"] = author, ["category"] = category,
    };
}

/// <summary>
/// M2b slice 5 (docs/specs/import.md): Echo of Dreams' blog comes over. Every published post keeps its uuid, slug, date,
/// tags and category; its gallery pictures are copied here and relinked; each author becomes a writer here under their
/// own name, with their EoD address and profile; nothing is announced; running it again brings nothing twice; and an
/// invitation lets an imported author sign in as themselves.
/// </summary>
public sealed class ImportTests
{
    private static readonly Guid Rain = Guid.Parse("0f4b1c3e-1111-4a9b-8c7d-000000000001");
    private static readonly Guid Porto = Guid.Parse("0f4b1c3e-1111-4a9b-8c7d-000000000002");
    private static readonly Guid Notes = Guid.Parse("0f4b1c3e-1111-4a9b-8c7d-000000000003");
    private static JsonObject Anna => new() { ["slug"] = "anna", ["displayName"] = "Anna Nováková", ["avatarUrl"] = null };

    private sealed class Move : IAsyncDisposable
    {
        public required PostTests.World W { get; init; }
        public required StubEod Eod { get; init; }
        public required FakeWeb Web { get; init; }
        public required string Blog { get; init; }

        public static async Task<Move> StartAsync()
        {
            var eod = new StubEod();
            var web = new FakeWeb();
            var w = await PostTests.World.StartAsync(b =>
            {
                MediaHost.Configure(new FakeBucket(), web)(b);
                b.UseSetting("Blog:Import:IntervalSeconds", "0");
                b.ConfigureTestServices(s => s.AddHttpClient(ImportJobs.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => eod));
            });
            var blog = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
            return new Move { W = w, Eod = eod, Web = web, Blog = blog };
        }

        /// <summary>The owner starts an import, and the worker runs it through.</summary>
        public async Task<JsonNode> ImportAsync(Dictionary<string, string>? authorMap = null, bool updateChanged = false)
        {
            var start = await W.Owner.PostJsonAsync($"/api/blogs/{Blog}/imports",
                new { source_url = StubEod.Api + "/", profile_base = "https://chat.eod.test", author_map = authorMap, update_changed = updateChanged }, csrf: true);
            Assert.True(start.Status == HttpStatusCode.Created, start.Json?.ToJsonString());
            Assert.True(await W.Host.App.Factory.Services.GetRequiredService<ImportWorker>().RunOnceAsync(default));
            return (await W.Owner.GetJsonAsync($"/api/blogs/{Blog}/imports")).Json!["imports"]![0]!;
        }

        public async Task<string> PageAsync(string path)
        {
            var r = await W.Host.Browser().Http.GetAsync(path);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{path}: {r.StatusCode}");
            return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
        }

        public async Task<T> SqlAsync<T>(string sql, object? args = null)
        {
            await using var conn = new NpgsqlConnection(W.Host.App.OwnerConnectionString);
            return await conn.QuerySingleAsync<T>(sql, args);
        }

        public ValueTask DisposeAsync() => W.DisposeAsync();
    }

    private static async Task<Move> WithEodBlogAsync()
    {
        var m = await Move.StartAsync();
        m.Eod.Posts.Add(StubEod.Post(Rain, "rain-in-lisbon", "Rain in Lisbon",
            "It rained.\n\n![The tram in the rain](g://travel/rain \"Tram 28\")\n\nThen it rained more.\n\n![A private one](gi://9d1e0000-0000-4000-8000-000000000009)",
            "2025-03-01T09:00:00Z", Anna, tags: ["travel", "lisbon"], category: new JsonObject { ["id"] = 3, ["slug"] = "journal", ["name"] = "Journal" },
            excerpt: "A wet week."));
        m.Eod.Posts.Add(StubEod.Post(Porto, "porto", "Porto", "Bridges.", "2025-04-02T09:00:00Z", Anna, cover: "g://travel/porto"));
        m.Eod.Posts.Add(StubEod.Post(Notes, "notes", "Notes", "Unsigned notes.", "2025-01-15T09:00:00Z", null));
        m.Eod.Pictures["g://travel/rain"] = "https://media.eod.test/signed/rain.png";
        m.Eod.Pictures["g://travel/porto"] = "https://media.eod.test/signed/porto.gif";
        m.Web.Serve("https://media.eod.test/signed/rain.png", MediaHost.Png, "image/png");
        m.Web.Serve("https://media.eod.test/signed/porto.gif", MediaHost.Gif, "image/gif");
        return m;
    }

    [Fact]
    public async Task Every_post_comes_over_with_its_id_address_date_terms_pictures_and_author()
    {
        await using var m = await WithEodBlogAsync();
        // A post already here holds the slug "notes": the imported one moves aside, and its old address follows it.
        var mine = (await m.W.Owner.PostJsonAsync($"/api/blogs/{m.Blog}/posts", new { title = "Notes", body_md = "Mine." }, csrf: true)).Json!["post"]!;
        await m.W.Owner.PostJsonAsync($"/api/posts/{mine["id"]}/publish", new { base_revision = mine["revision"]!.GetValue<string>() }, csrf: true);

        var job = await m.ImportAsync();
        Assert.Equal("done", job["state"]!.GetValue<string>());
        var counts = job["counts"]!;
        Assert.Equal(3, counts["imported"]!.GetValue<int>());
        Assert.Equal(2, counts["pictures"]!.GetValue<int>());
        Assert.Equal(1, counts["pictures_missing"]!.GetValue<int>());
        Assert.Equal(1, counts["authors"]!.GetValue<int>());

        // Same ids, so EoD's comments stay attached; same addresses; the original date; live.
        var rain = (await m.W.Owner.GetJsonAsync($"/api/posts/{Rain}")).Json!;
        Assert.Equal("rain-in-lisbon", rain["post"]!["slug"]!.GetValue<string>());
        Assert.Equal("live", rain["post"]!["status"]!.GetValue<string>());
        Assert.StartsWith("2025-03-01T09:00:00", rain["post"]!["published_at"]!.GetValue<string>());
        Assert.Equal("A wet week.", rain["post"]!["excerpt"]!.GetValue<string>());
        Assert.Equal(["Journal", "lisbon", "travel"], rain["post"]!["terms"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Order());

        // The public picture is copied and relinked (its title kept); the private one becomes its description.
        var body = rain["body_md"]!.GetValue<string>();
        Assert.Matches(@"!\[The tram in the rain\]\(/media/[0-9a-f-]{36}/rain\.png ""Tram 28""\)", body);
        Assert.Contains("*A private one*", body);
        Assert.DoesNotContain("g://", body);
        Assert.Contains(job["log"]!.AsArray(), l => l!["text"]!.GetValue<string>().StartsWith("A picture in “Rain in Lisbon” (gi://", StringComparison.Ordinal)
                                                     && l["level"]!.GetValue<string>() == "warning");
        // A cover comes first in the body, so it's the post's picture on shares too.
        Assert.Matches(@"^!\[Porto\]\(/media/[0-9a-f-]{36}/[^)]+\.gif\)", (await m.W.Owner.GetJsonAsync($"/api/posts/{Porto}")).Json!["body_md"]!.GetValue<string>());

        // Readers: the byline names Anna; her page is at her EoD address with her EoD profile; the tag page works.
        var rainPage = await m.PageAsync("/posts/rain-in-lisbon");
        Assert.True(rainPage.Contains("<p class=\"byline\">By <a rel=\"author\" href=\"https://blog.test/author/anna\">Anna Nováková</a></p>"),
            System.Text.RegularExpressions.Regex.Match(rainPage, "<p class=\"byline\">.*?</p>").Value);
        var page = await m.PageAsync("/author/anna");
        Assert.Contains("Rain in Lisbon", page);
        Assert.Contains("https://chat.eod.test/creator/anna", page);
        Assert.Contains("Rain in Lisbon", await m.PageAsync("/tag/travel"));
        Assert.Contains("Rain in Lisbon", await m.PageAsync("/category/journal"));
        // A post with no author is the owner's.
        Assert.Contains("By <a rel=\"author\" href=\"https://blog.test/author/lenka\">Lenka</a>", await m.PageAsync("/posts/notes-2"));

        // EoD's addresses: the old /{slug} moves by itself; the renamed one through its row.
        var browser = m.W.Host.Browser().Http;
        Assert.Equal("/posts/rain-in-lisbon", (await browser.GetAsync("/rain-in-lisbon")).Headers.Location!.OriginalString);
        Assert.Equal(1, await m.SqlAsync<int>("SELECT count(*)::int FROM old_address WHERE path = '/notes' AND target = '/posts/notes-2'"));
        // ...but the slug "notes" is the owner's own post now, which answers itself.
        Assert.Contains("Mine.", await m.PageAsync($"/posts/{mine["slug"]}"));
    }

    [Fact]
    public async Task Nothing_imported_is_announced_or_sent_and_history_starts_at_the_import()
    {
        await using var m = await WithEodBlogAsync();
        await m.ImportAsync();
        Assert.Equal(0, await m.SqlAsync<int>("SELECT count(*)::int FROM event WHERE kind = 'post_published'"));
        Assert.Equal(3, await m.SqlAsync<int>("SELECT count(*)::int FROM event WHERE kind = 'post_imported'"));
        var revisions = (await m.W.Owner.GetJsonAsync($"/api/posts/{Rain}")).Json!["revisions"]!.AsArray();
        Assert.Equal("Anna Nováková", Assert.Single(revisions)!["by_label"]!.GetValue<string>());
        // The announcer finds nothing new to say about them.
        Assert.Equal(0, await m.W.Host.App.Factory.Services.GetRequiredService<AgentBlog.Announce.Announcer>().CreatePassAsync(default));
    }

    [Fact]
    public async Task Running_it_again_brings_nothing_twice()
    {
        await using var m = await WithEodBlogAsync();
        await m.ImportAsync();
        m.Eod.Posts.Add(StubEod.Post(Guid.Parse("0f4b1c3e-1111-4a9b-8c7d-000000000004"), "later", "Later", "New on EoD.", "2025-05-01T09:00:00Z", Anna));

        var again = await m.ImportAsync();
        Assert.Equal(1, again["counts"]!["imported"]!.GetValue<int>());
        Assert.Equal(3, again["counts"]!["already_here"]!.GetValue<int>());
        Assert.Null(again["counts"]!["authors"]);
        Assert.Equal(1, await m.SqlAsync<int>("SELECT count(*)::int FROM account WHERE display_name = 'Anna Nováková'"));
        Assert.Equal(4, await m.SqlAsync<int>("SELECT count(*)::int FROM post"));
        // Details are asked only for what isn't here yet.
        Assert.Single(m.Eod.Asked, a => a == "GET /api/blog/posts/rain-in-lisbon");
    }

    [Fact]
    public async Task Update_mode_brings_a_source_change_as_a_new_revision_and_leaves_an_unchanged_post_alone()
    {
        await using var m = await WithEodBlogAsync();
        await m.ImportAsync();
        Assert.Equal(1, await m.SqlAsync<int>("SELECT count(*)::int FROM post_revision WHERE post_id = @id", new { id = Rain }));

        // Rain changes at the source (new words, a newer updatedAt); Porto and Notes stay as they were.
        var rain = m.Eod.Posts.Single(p => p["uuid"]!.GetValue<Guid>() == Rain);
        rain["content"] = "It rained more now.";
        rain["updatedAt"] = "2025-06-01T09:00:00Z";

        var again = await m.ImportAsync(updateChanged: true);
        Assert.Equal("done", again["state"]!.GetValue<string>());
        Assert.Equal(1, again["counts"]!["updated"]!.GetValue<int>());
        Assert.Equal(2, again["counts"]!["unchanged"]!.GetValue<int>());
        Assert.Null(again["counts"]!["imported"]);
        Assert.Null(again["counts"]!["skipped_local"]);

        // A new revision (the history stays) with the source's new words; Porto untouched.
        Assert.Equal(2, await m.SqlAsync<int>("SELECT count(*)::int FROM post_revision WHERE post_id = @id", new { id = Rain }));
        Assert.Equal("It rained more now.", (await m.W.Owner.GetJsonAsync($"/api/posts/{Rain}")).Json!["body_md"]!.GetValue<string>());
        Assert.Equal(1, await m.SqlAsync<int>("SELECT count(*)::int FROM post_revision WHERE post_id = @id", new { id = Porto }));
        // An update is no more news than the import: nothing published, one post_import_updated event.
        Assert.Equal(0, await m.SqlAsync<int>("SELECT count(*)::int FROM event WHERE kind = 'post_published'"));
        Assert.Equal(1, await m.SqlAsync<int>("SELECT count(*)::int FROM event WHERE kind = 'post_import_updated'"));
    }

    [Fact]
    public async Task Update_mode_never_overwrites_a_post_edited_here_after_the_import()
    {
        await using var m = await WithEodBlogAsync();
        await m.ImportAsync();
        // The owner edits Rain here after the import: a revision of their own.
        var read = (await m.W.Owner.GetJsonAsync($"/api/posts/{Rain}")).Json!;
        Assert.Equal(HttpStatusCode.OK, (await m.W.Owner.SendAsync(HttpMethod.Patch, $"/api/posts/{Rain}", csrf: true,
            body: new { base_revision = read["post"]!["revision"]!.GetValue<string>(), body_md = "Edited here." })).Status);

        // The source changes too, newer than the import.
        var rain = m.Eod.Posts.Single(p => p["uuid"]!.GetValue<Guid>() == Rain);
        rain["content"] = "Changed at the source.";
        rain["updatedAt"] = "2025-06-01T09:00:00Z";

        var again = await m.ImportAsync(updateChanged: true);
        Assert.Equal("done", again["state"]!.GetValue<string>());
        Assert.Equal(1, again["counts"]!["skipped_local"]!.GetValue<int>());
        Assert.Null(again["counts"]!["updated"]);
        // The local words stay; no new revision; the log names the post.
        Assert.Equal("Edited here.", (await m.W.Owner.GetJsonAsync($"/api/posts/{Rain}")).Json!["body_md"]!.GetValue<string>());
        Assert.Equal(2, await m.SqlAsync<int>("SELECT count(*)::int FROM post_revision WHERE post_id = @id", new { id = Rain }));
        Assert.Contains(again["log"]!.AsArray(), l => l!["level"]!.GetValue<string>() == "warning"
            && l["text"]!.GetValue<string>().Contains("Rain in Lisbon") && l["text"]!.GetValue<string>().Contains("edited here"));
    }

    [Fact]
    public async Task Update_mode_leaves_a_trashed_post_alone_and_names_it_in_the_log()
    {
        await using var m = await WithEodBlogAsync();
        await m.ImportAsync();
        // The owner trashes Rain here; the source then changes it.
        Assert.Equal(HttpStatusCode.OK, (await m.W.Owner.PostJsonAsync($"/api/posts/{Rain}/trash", new { }, csrf: true)).Status);
        var rain = m.Eod.Posts.Single(p => p["uuid"]!.GetValue<Guid>() == Rain);
        rain["content"] = "Changed at the source.";
        rain["updatedAt"] = "2025-06-01T09:00:00Z";

        var again = await m.ImportAsync(updateChanged: true);
        Assert.Equal("done", again["state"]!.GetValue<string>());
        Assert.Null(again["counts"]!["updated"]);
        Assert.Equal(1, again["counts"]!["skipped_trash"]!.GetValue<int>());
        // No new revision; the post stays in the trash with its own words; the log names it.
        Assert.Equal(1, await m.SqlAsync<int>("SELECT count(*)::int FROM post_revision WHERE post_id = @id", new { id = Rain }));
        var read = (await m.W.Owner.GetJsonAsync($"/api/posts/{Rain}")).Json!;
        Assert.Equal("trash", read["post"]!["status"]!.GetValue<string>());
        Assert.Contains("It rained.", read["body_md"]!.GetValue<string>());
        Assert.Contains(again["log"]!.AsArray(), l => l!["level"]!.GetValue<string>() == "warning"
            && l["text"]!.GetValue<string>().Contains("Rain in Lisbon") && l["text"]!.GetValue<string>().Contains("trash"));
    }

    [Fact]
    public async Task A_seven_digit_source_timestamp_does_not_churn_revisions_on_update_runs()
    {
        await using var m = await Move.StartAsync();
        // A source timestamp finer than timestamptz keeps (whole microseconds), at a non-UTC offset:
        // 11:00+02:00 is 09:00Z, and .1234562s lands as .123456 in the store.
        m.Eod.Posts.Add(StubEod.Post(Rain, "rain-in-lisbon", "Rain in Lisbon", "![The tram](g://travel/rain)", "2025-03-01T11:00:00.1234562+02:00", null));
        m.Eod.Pictures["g://travel/rain"] = "https://media.eod.test/signed/rain.png";
        m.Web.Serve("https://media.eod.test/signed/rain.png", MediaHost.Png, "image/png");
        var first = await m.ImportAsync();
        Assert.Equal("done", first["state"]!.GetValue<string>());
        Assert.Equal(1, first["counts"]!["pictures"]!.GetValue<int>());
        // The instant is kept, not re-labelled to the machine's zone: published_at is 09:00Z.
        Assert.Equal(new DateTime(2025, 3, 1, 9, 0, 0, DateTimeKind.Utc).AddTicks(1234560),
            await m.SqlAsync<DateTime>("SELECT published_at FROM post WHERE id = @id", new { id = Rain }));
        Assert.Equal(1, await m.SqlAsync<int>("SELECT count(*)::int FROM post_revision WHERE post_id = @id", new { id = Rain }));

        // Two update runs with the source unchanged: both leave it alone, no new revision, no pictures copied again.
        for (var run = 0; run < 2; run++)
        {
            var again = await m.ImportAsync(updateChanged: true);
            Assert.Equal("done", again["state"]!.GetValue<string>());
            Assert.Null(again["counts"]!["updated"]);
            Assert.Equal(1, again["counts"]!["unchanged"]!.GetValue<int>());
            Assert.Null(again["counts"]!["pictures"]);
            Assert.Equal(1, await m.SqlAsync<int>("SELECT count(*)::int FROM post_revision WHERE post_id = @id", new { id = Rain }));
        }
    }

    [Fact]
    public async Task An_imported_post_keeps_the_sources_feed_id_in_the_feeds_and_a_new_post_keeps_its_own()
    {
        await using var m = await Move.StartAsync();
        // One post gives its own feed id; another gives none and keeps this blog's own absolute address.
        var withId = StubEod.Post(Rain, "rain-in-lisbon", "Rain in Lisbon", "It rained.", "2025-03-01T09:00:00Z", null);
        withId["feedId"] = "https://source.example/feeds/rain";
        m.Eod.Posts.Add(withId);
        m.Eod.Posts.Add(StubEod.Post(Porto, "porto", "Porto", "Bridges.", "2025-04-02T09:00:00Z", null));
        await m.ImportAsync();

        // A post written here keeps its own id (its address).
        var mine = (await m.W.Owner.PostJsonAsync($"/api/blogs/{m.Blog}/posts", new { title = "Mine", body_md = "Here." }, csrf: true)).Json!["post"]!;
        await m.W.Owner.PostJsonAsync($"/api/posts/{mine["id"]}/publish", new { base_revision = mine["revision"]!.GetValue<string>() }, csrf: true);

        // Stored on import: the source's field; none for a post with no source id, or written here.
        Assert.Equal("https://source.example/feeds/rain", await m.SqlAsync<string>("SELECT source_feed_id FROM post WHERE id = @id", new { id = Rain }));
        Assert.Null(await m.SqlAsync<string?>("SELECT source_feed_id FROM post WHERE id = @id", new { id = Porto }));
        Assert.Null(await m.SqlAsync<string?>("SELECT source_feed_id FROM post WHERE id = @id::uuid", new { id = mine["id"]!.GetValue<string>() }));

        var browser = m.W.Host.Browser().Http;
        var atom = await (await browser.GetAsync("/atom.xml")).Content.ReadAsStringAsync();
        var rss = await (await browser.GetAsync("/feed.xml")).Content.ReadAsStringAsync();
        // Atom entry ids: the imported post with a source id carries it; the others carry their own absolute address.
        Assert.Contains(">https://source.example/feeds/rain</id>", atom);
        Assert.Contains(">https://blog.test/posts/porto</id>", atom);
        Assert.Contains(">https://blog.test/posts/mine</id>", atom);
        // RSS guids: the source id is not a permalink; the posts' own addresses are.
        Assert.Contains("<guid isPermaLink=\"false\">https://source.example/feeds/rain</guid>", rss);
        Assert.Contains("<guid isPermaLink=\"true\">https://blog.test/posts/porto</guid>", rss);
        Assert.Contains("<guid isPermaLink=\"true\">https://blog.test/posts/mine</guid>", rss);
    }

    [Fact]
    public async Task A_feed_id_that_is_not_a_usable_IRI_is_stored_as_null_and_the_feeds_still_answer()
    {
        await using var m = await Move.StartAsync();
        // An XML-invalid character, and an id over the 2,000-character cap: both import, both store NULL.
        var bad = StubEod.Post(Rain, "bad-id", "Bad id", "Words.", "2025-03-01T09:00:00Z", null);
        bad["feedId"] = "bad" + (char)1 + "id";
        var tooLong = StubEod.Post(Porto, "long-id", "Long id", "Words.", "2025-04-02T09:00:00Z", null);
        tooLong["feedId"] = "https://source.example/" + new string('a', 5000);
        // A relative address is not an absolute http(s) URI either; exactly 2,000 characters is still kept.
        var relative = StubEod.Post(Notes, "relative-id", "Relative id", "Words.", "2025-05-02T09:00:00Z", null);
        relative["feedId"] = "feeds/relative";
        var atCap = Guid.Parse("0f4b1c3e-1111-4a9b-8c7d-000000000004");
        var cap = StubEod.Post(atCap, "cap-id", "Cap id", "Words.", "2025-06-02T09:00:00Z", null);
        cap["feedId"] = "https://source.example/" + new string('a', 2000 - "https://source.example/".Length);
        m.Eod.Posts.Add(bad);
        m.Eod.Posts.Add(tooLong);
        m.Eod.Posts.Add(relative);
        m.Eod.Posts.Add(cap);

        var job = await m.ImportAsync();
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Equal(4, job["counts"]!["imported"]!.GetValue<int>());
        Assert.Null(await m.SqlAsync<string?>("SELECT source_feed_id FROM post WHERE id = @id", new { id = Rain }));
        Assert.Null(await m.SqlAsync<string?>("SELECT source_feed_id FROM post WHERE id = @id", new { id = Porto }));
        Assert.Null(await m.SqlAsync<string?>("SELECT source_feed_id FROM post WHERE id = @id", new { id = Notes }));
        var capId = cap["feedId"]!.GetValue<string>();
        Assert.Equal(capId, await m.SqlAsync<string>("SELECT source_feed_id FROM post WHERE id = @id", new { id = atCap }));

        // The feeds answer 200: the unusable ids never reach the XML, and each post keeps its own absolute address.
        var atom = await m.PageAsync("/atom.xml");
        var rss = await m.PageAsync("/feed.xml");
        Assert.Contains(">https://blog.test/posts/bad-id</id>", atom);
        Assert.Contains(">https://blog.test/posts/long-id</id>", atom);
        Assert.Contains(">https://blog.test/posts/relative-id</id>", atom);
        Assert.Contains(">" + capId + "</id>", atom);
        Assert.Contains("<guid isPermaLink=\"true\">https://blog.test/posts/bad-id</guid>", rss);
        Assert.Contains("<guid isPermaLink=\"true\">https://blog.test/posts/long-id</guid>", rss);
    }

    /// <summary>
    /// The feed id forms a source can give (docs/specs/import.md): an absolute http(s) address with a host, an IDN host
    /// included, and Atom's other canonical id forms, a <c>urn:</c> (<c>urn:uuid:</c>) and a <c>tag:</c> URI. Anything a
    /// reader could be sent to that isn't an entry id, a relative id, or one with whitespace or a control or XML-invalid
    /// character, is null.
    /// </summary>
    [Theory]
    [InlineData("https://source.example/feeds/rain", true)]
    [InlineData("http://source.example/?p=12", true)]
    [InlineData("https://bücher.example/posts/1", true)]
    [InlineData("https://xn--bcher-kva.example/posts/1", true)]
    [InlineData("urn:uuid:1225c695-cfb8-4ebb-aaaa-80da344efa6a", true)]
    [InlineData("URN:UUID:1225C695-CFB8-4EBB-AAAA-80DA344EFA6A", true)]
    [InlineData("urn:isbn:0451450523", true)]
    [InlineData("tag:source.example,2025:rain", true)]
    [InlineData("tag:anna@source.example,2025-03-01:posts/rain", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,<b>x</b>", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("/feeds/rain", false)]
    [InlineData("feeds/rain", false)]
    [InlineData("//source.example/feeds/rain", false)]
    [InlineData("https:feeds/rain", false)]
    [InlineData("https:///nohost", false)]
    [InlineData("urn:x:", false)]
    [InlineData("urn:uuid", false)]
    [InlineData("tag:source.example:rain", false)]
    [InlineData("https://source.example/a b", false)]
    [InlineData(" https://source.example/a", false)]
    [InlineData("https://source.example/a\t", false)]
    [InlineData("https://source.example/a\nb", false)]
    [InlineData("urn:uuid:1225c695 cfb8", false)]
    [InlineData("tag:source.example,2025:a\u0085b", false)]
    [InlineData("https://source.example/a\u0001", false)]
    [InlineData("https://source.example/\uFFFE", false)]
    [InlineData("", false)]
    public void A_feed_id_is_kept_only_in_a_form_feeds_use_for_an_entry_id(string given, bool kept) =>
        Assert.Equal(kept ? given : null, AgentBlog.Posts.PostService.CleanFeedId(given));

    [Fact]
    public void A_lone_surrogate_in_a_feed_id_is_refused()
    {
        Assert.Null(AgentBlog.Posts.PostService.CleanFeedId("https://source.example/" + (char)0xD800));
        Assert.Equal("https://source.example/" + char.ConvertFromUtf32(0x1F600),
            AgentBlog.Posts.PostService.CleanFeedId("https://source.example/" + char.ConvertFromUtf32(0x1F600)));
    }

    [Fact]
    public async Task A_source_title_with_a_tab_or_line_break_arrives_tidied_and_urn_and_tag_ids_reach_the_feeds()
    {
        await using var m = await Move.StartAsync();
        // EoD titles with what a title here can't hold: a tab, wrapped lines, a control character. None fails its post.
        var tabbed = StubEod.Post(Rain, "rain-in-lisbon", "Rain\tin  Lisbon", "It rained.", "2025-03-01T09:00:00Z", null);
        tabbed["feedId"] = "tag:source.example,2025:rain";
        var wrapped = StubEod.Post(Porto, "porto", "  Porto \r\n by night\n", "Bridges.", "2025-04-02T09:00:00Z", null);
        wrapped["feedId"] = "urn:uuid:1225c695-cfb8-4ebb-aaaa-80da344efa6a";
        var bell = StubEod.Post(Notes, "notes", "Notes" + (char)7 + " from" + (char)10 + "afar", "Words.", "2025-05-02T09:00:00Z", null);
        m.Eod.Posts.Add(tabbed);
        m.Eod.Posts.Add(wrapped);
        m.Eod.Posts.Add(bell);

        var job = await m.ImportAsync();
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Equal(3, job["counts"]!["imported"]!.GetValue<int>());
        var titles = await m.SqlAsync<string>("""
            SELECT string_agg(r.title, '|' ORDER BY p.slug) FROM post p JOIN post_revision r ON r.id = p.current_revision_id
            """);
        // A tab or line break and the spaces around it become one space; spaces elsewhere stay; the bell is dropped.
        Assert.Equal("Notes from afar|Porto by night|Rain in  Lisbon", titles);

        var rss = System.Xml.Linq.XDocument.Parse(await m.PageAsync("/feed.xml"));
        var atom = System.Xml.Linq.XDocument.Parse(await m.PageAsync("/atom.xml"));
        System.Xml.Linq.XNamespace a = "http://www.w3.org/2005/Atom";
        Assert.Contains("tag:source.example,2025:rain", rss.Descendants("guid").Select(g => g.Value));
        Assert.Contains("urn:uuid:1225c695-cfb8-4ebb-aaaa-80da344efa6a", rss.Descendants("guid").Select(g => g.Value));
        var ids = atom.Descendants(a + "entry").Select(e => e.Element(a + "id")!.Value).ToList();
        Assert.Contains("tag:source.example,2025:rain", ids);
        Assert.Contains("urn:uuid:1225c695-cfb8-4ebb-aaaa-80da344efa6a", ids);
        Assert.Contains("https://blog.test/posts/notes", ids);
    }

    [Fact]
    public async Task A_categories_endpoint_that_answers_not_found_is_an_empty_list_not_a_failed_run()
    {
        await using var m = await WithEodBlogAsync();
        m.Eod.CategoriesFails = HttpStatusCode.NotFound;
        var job = await m.ImportAsync();
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Equal(3, job["counts"]!["imported"]!.GetValue<int>());
        Assert.Null(job["counts"]!["failed"]);

        // A 5xx still stops the run with its reason, as before.
        m.Eod.CategoriesFails = HttpStatusCode.InternalServerError;
        Assert.Equal(HttpStatusCode.Created, (await m.W.Owner.PostJsonAsync($"/api/blogs/{m.Blog}/imports", new { source_url = StubEod.Api }, csrf: true)).Status);
        Assert.True(await m.W.Host.App.Factory.Services.GetRequiredService<ImportWorker>().RunOnceAsync(default));
        var failed = (await m.W.Owner.GetJsonAsync($"/api/blogs/{m.Blog}/imports")).Json!["imports"]![0]!;
        Assert.Equal("failed", failed["state"]!.GetValue<string>());
        Assert.Contains("500", failed["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_imported_author_claims_their_account_from_an_invitation_and_their_posts_are_theirs()
    {
        await using var m = await WithEodBlogAsync();
        await m.ImportAsync();
        var people = (await m.W.Owner.GetJsonAsync($"/api/blogs/{m.Blog}/people")).Json!["people"]!.AsArray();
        var anna = people.Single(p => p!["name"]!.GetValue<string>() == "Anna Nováková")!;
        Assert.False(anna["signed_in"]!.GetValue<bool>());
        Assert.Equal("writer", anna["role"]!.GetValue<string>());

        var invite = await m.W.Owner.PostJsonAsync($"/api/blogs/{m.Blog}/invitations",
            new { role = "writer", note = "Anna", account = anna["account"]!.GetValue<string>() }, csrf: true);
        Assert.Equal(HttpStatusCode.Created, invite.Status);
        var link = invite.Json!["link"]!.GetValue<string>();
        var browser = m.W.Host.Browser();
        var begin = await browser.PostJsonAsync("/api/auth/invite", new { token = link[(link.LastIndexOf('/') + 1)..], provider = "discord" });
        var landed = await browser.CompleteAtProviderAsync(new Uri(begin.Json!["url"]!.GetValue<string>()), new StubAccount("600000000000000001", "anna_n", null));
        Assert.Equal("/admin?joined=1", landed.Location);

        var me = (await browser.GetJsonAsync("/api/me")).Json!;
        Assert.Equal(anna["account"]!.GetValue<string>(), me["account"]!["id"]!.GetValue<string>());
        Assert.True((await browser.GetJsonAsync($"/api/posts/{Rain}")).Json!["can"]!["mine"]!.GetValue<bool>());
        Assert.Equal(1, await m.SqlAsync<int>("SELECT count(*)::int FROM account WHERE display_name = 'Anna Nováková'"));

        // Claimed now: a second claim on that account is refused.
        var twice = await m.W.Owner.PostJsonAsync($"/api/blogs/{m.Blog}/invitations",
            new { role = "writer", account = anna["account"]!.GetValue<string>() }, csrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, twice.Status);
    }

    [Fact]
    public async Task Only_the_owner_imports_from_an_https_address_one_at_a_time_and_a_failure_says_why()
    {
        await using var c = await PeoplesAgentsTests.Crew.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await c.Ben.PostJsonAsync($"/api/blogs/{c.Blog}/imports", new { source_url = StubEod.Api }, csrf: true)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Ben.GetJsonAsync($"/api/blogs/{c.Blog}/imports")).Status);
        foreach (var bad in new[] { "http://eod.test", "https://user:pw@eod.test", "eod.test", "https://eod.test/?x=1" })
            Assert.True((await c.Owner.PostJsonAsync($"/api/blogs/{c.Blog}/imports", new { source_url = bad }, csrf: true)).Status == HttpStatusCode.BadRequest, bad);

        // The rule lives in the core, not only in the API: an editor can't start one there either.
        var core = c.Host.App.Factory.Services.GetRequiredService<ImportJobs>();
        var benId = Guid.Parse(await c.AccountIdAsync("Ben"));
        var (job, why) = await core.StartAsync(AgentBlog.Posts.Actor.Person(Guid.Parse(c.Blog), benId, "editor"), StubEod.Api, null, null, default);
        Assert.Null(job);
        Assert.Equal("Only the owner imports.", why);

        await using var m = await WithEodBlogAsync();
        m.Eod.ListFails = HttpStatusCode.InternalServerError;
        Assert.Equal(HttpStatusCode.Created, (await m.W.Owner.PostJsonAsync($"/api/blogs/{m.Blog}/imports", new { source_url = StubEod.Api }, csrf: true)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await m.W.Owner.PostJsonAsync($"/api/blogs/{m.Blog}/imports", new { source_url = StubEod.Api }, csrf: true)).Status);
        await m.W.Host.App.Factory.Services.GetRequiredService<ImportWorker>().RunOnceAsync(default);
        var failed = (await m.W.Owner.GetJsonAsync($"/api/blogs/{m.Blog}/imports")).Json!["imports"]![0]!;
        Assert.Equal("failed", failed["state"]!.GetValue<string>());
        Assert.Contains("500", failed["error"]!.GetValue<string>());
        // Fixed on EoD's side, it runs again.
        m.Eod.ListFails = null;
        Assert.Equal("done", (await m.ImportAsync())["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_post_with_more_than_100_gallery_refs_resolves_them_in_chunks_and_imports_them_all()
    {
        await using var m = await Move.StartAsync();
        var refs = Enumerable.Range(0, 101).Select(i => $"g://many/p{i}").ToList();
        m.Eod.Posts.Add(StubEod.Post(Rain, "many-pictures", "Many pictures", string.Join("\n\n", refs.Select(r => $"![Pic]({r})")),
            "2025-03-01T09:00:00Z", null));
        foreach (var r in refs) m.Eod.Pictures[r] = "https://media.eod.test/signed/one.png";
        m.Web.Serve("https://media.eod.test/signed/one.png", MediaHost.Png, "image/png");

        var job = await m.ImportAsync();
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Equal(1, job["counts"]!["imported"]!.GetValue<int>());
        Assert.Equal(101, job["counts"]!["pictures"]!.GetValue<int>());
        Assert.Null(job["counts"]!["pictures_missing"]);
        Assert.Null(job["counts"]!["failed"]);
        // EoD refuses more than 100 refs in one call: the importer asked in chunks of at most 100.
        Assert.Equal(2, m.Eod.Asked.Count(a => a == "POST /api/pictures/resolve"));
        Assert.DoesNotContain("g://", (await m.W.Owner.GetJsonAsync($"/api/posts/{Rain}")).Json!["body_md"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_second_blog_importing_the_same_source_is_not_told_already_here()
    {
        await using var m = await WithEodBlogAsync();
        await m.ImportAsync();
        var ownerId = (await m.W.Owner.GetJsonAsync("/api/me")).Json!["account"]!["id"]!.GetValue<string>();

        // A second blog on the same instance, the same owner, importing the same source.
        var blog2 = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(m.W.Host.App.OwnerConnectionString))
        {
            await conn.ExecuteAsync("INSERT INTO blog (id, slug, title, created_at) VALUES (@blog2, 'second', 'Second blog', now())", new { blog2 });
            await conn.ExecuteAsync("INSERT INTO blog_member (blog_id, account_id, role, created_at) VALUES (@blog2, @owner::uuid, 'owner', now())",
                new { blog2, owner = Guid.Parse(ownerId) });
        }
        Assert.Equal(HttpStatusCode.Created, (await m.W.Owner.PostJsonAsync($"/api/blogs/{blog2}/imports", new { source_url = StubEod.Api + "/" }, csrf: true)).Status);
        Assert.True(await m.W.Host.App.Factory.Services.GetRequiredService<ImportWorker>().RunOnceAsync(default));
        var job = (await m.W.Owner.GetJsonAsync($"/api/blogs/{blog2}/imports")).Json!["imports"]![0]!;

        // Not "already here" (they are on the other blog): each post fails with the reason, and nothing lands twice.
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Null(job["counts"]!["already_here"]);
        Assert.Equal(3, job["counts"]!["failed"]!.GetValue<int>());
        Assert.Contains(job["log"]!.AsArray(), l => l!["text"]!.GetValue<string>().Contains("another blog"));
        Assert.Equal(3, await m.SqlAsync<int>("SELECT count(*)::int FROM post WHERE blog_id = @blog::uuid", new { blog = Guid.Parse(m.Blog) }));
        await NothingOnBlog2Async(m, blog2);

        // A second run piles up nothing either: the refusal happens before anything is fetched or made.
        Assert.Equal(HttpStatusCode.Created, (await m.W.Owner.PostJsonAsync($"/api/blogs/{blog2}/imports", new { source_url = StubEod.Api + "/" }, csrf: true)).Status);
        Assert.True(await m.W.Host.App.Factory.Services.GetRequiredService<ImportWorker>().RunOnceAsync(default));
        var twice = (await m.W.Owner.GetJsonAsync($"/api/blogs/{blog2}/imports")).Json!["imports"]![0]!;
        Assert.Equal("done", twice["state"]!.GetValue<string>());
        Assert.Equal(3, twice["counts"]!["failed"]!.GetValue<int>());
        await NothingOnBlog2Async(m, blog2);
        // EoD was asked for post details only on the first blog's run: the pre-check never fetches.
        Assert.Equal(3, m.Eod.Asked.Count(a => a.StartsWith("GET /api/blog/posts/", StringComparison.Ordinal)));
    }

    private static async Task NothingOnBlog2Async(Move m, Guid blog2)
    {
        Assert.Equal(0, await m.SqlAsync<int>("SELECT count(*)::int FROM post WHERE blog_id = @blog2", new { blog2 }));
        Assert.Equal(0, await m.SqlAsync<int>("SELECT count(*)::int FROM media WHERE blog_id = @blog2", new { blog2 }));
        Assert.Equal(0, await m.SqlAsync<int>("SELECT count(*)::int FROM blog_member WHERE blog_id = @blog2 AND role = 'writer'", new { blog2 }));
        Assert.Equal(0, await m.SqlAsync<int>("SELECT count(*)::int FROM import_author WHERE blog_id = @blog2", new { blog2 }));
    }

    [Fact]
    public async Task A_post_named_themes_keeps_its_slug_and_the_themes_address_answers_by_the_picker()
    {
        await using var m = await Move.StartAsync();
        m.Eod.Posts.Add(StubEod.Post(Rain, "themes", "Themes", "A post named themes.", "2025-03-01T09:00:00Z", null));

        var job = await m.ImportAsync();
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Equal(1, job["counts"]!["imported"]!.GetValue<int>());

        // The post keeps its slug; no row is written for it.
        Assert.Equal("themes", (await m.W.Owner.GetJsonAsync($"/api/posts/{Rain}")).Json!["post"]!["slug"]!.GetValue<string>());
        Assert.Contains("A post named themes.", await m.PageAsync("/posts/themes"));
        Assert.Equal(0, await m.SqlAsync<int>("SELECT count(*)::int FROM old_address WHERE path = '/themes'"));

        // With the theme picker off, /themes is not a page: it falls through to the old-address rule, which moves
        // a live post's /{slug} by itself.
        var browser = m.W.Host.Browser().Http;
        var off = await browser.GetAsync("/themes");
        Assert.Equal(HttpStatusCode.MovedPermanently, off.StatusCode);
        Assert.Equal("/posts/themes", off.Headers.Location!.OriginalString);

        // With the picker on, the themes page is real and wins; the post stays at /posts/themes.
        Assert.Equal(HttpStatusCode.OK, (await m.W.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{m.Blog}", csrf: true, body: new { theme_picker = true })).Status);
        var on = await browser.GetAsync("/themes");
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.DoesNotContain("A post named themes.", await on.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Terms_keep_the_source_address_renamed_ones_move_and_unaddressable_tags_are_left_off_not_fatal()
    {
        await using var m = await Move.StartAsync();
        m.Eod.Posts.Add(StubEod.Post(Rain, "term-addresses", "Term addresses", "Words.", "2025-03-01T09:00:00Z", null,
            tags: ["world_building", "日本語", "Travel"],
            category: new JsonObject { ["id"] = 7, ["slug"] = "devlogs", ["name"] = "Dev Logs" }));

        var job = await m.ImportAsync();
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Equal(1, job["counts"]!["imported"]!.GetValue<int>());
        Assert.Null(job["counts"]!["failed"]);

        // The category is at its source slug, not at the name's re-slugification ("dev-logs").
        Assert.Contains("Term addresses", await m.PageAsync("/category/devlogs"));
        // The post carries the category and the two addressable tags, under their own names.
        var terms = (await m.W.Owner.GetJsonAsync($"/api/posts/{Rain}")).Json!["post"]!["terms"]!.AsArray();
        Assert.Equal(["Dev Logs", "Travel", "world_building"], terms.Select(t => t!["name"]!.GetValue<string>()).Order());

        // EoD's tag addresses (the name as written, escaped) move to the slugs they got here.
        var browser = m.W.Host.Browser().Http;
        Assert.Equal("/tag/world-building", (await browser.GetAsync("/tag/world_building")).Headers.Location!.OriginalString);
        Assert.Equal("/tag/travel", (await browser.GetAsync("/tag/Travel")).Headers.Location!.OriginalString);
        Assert.Contains("Term addresses", await m.PageAsync("/tag/world-building"));

        // The tag with no A–Z/0–9 was left off and logged; the post still came over.
        Assert.Equal(0, await m.SqlAsync<int>("SELECT count(*)::int FROM term WHERE name = '日本語'"));
        Assert.Equal(1, job["counts"]!["terms_dropped"]!.GetValue<int>());
        Assert.Contains(job["log"]!.AsArray(), l => l!["level"]!.GetValue<string>() == "warning" && l["text"]!.GetValue<string>().Contains("日本語"));
    }

    [Fact]
    public async Task A_long_tag_name_and_a_long_excerpt_are_cut_without_splitting_an_emoji()
    {
        await using var m = await Move.StartAsync();
        var smile = char.ConvertFromUtf32(0x1F600);
        // The emoji's two halves sit across each cut: the tag name's (100) and the excerpt's (1,000 with its ellipsis).
        var tag = new string('a', AgentBlog.Posts.Taxonomy.MaxName - 1) + smile + "b";
        var excerpt = new string('x', AgentBlog.Posts.PostService.MaxExcerpt - 2) + smile + "y";
        m.Eod.Posts.Add(StubEod.Post(Rain, "long-cuts", "Long cuts", "Words.", "2025-03-01T09:00:00Z", null, tags: [tag], excerpt: excerpt));

        var job = await m.ImportAsync();
        Assert.Equal(1, job["counts"]!["imported"]!.GetValue<int>());

        var post = (await m.W.Owner.GetJsonAsync($"/api/posts/{Rain}")).Json!["post"]!;
        var name = Assert.Single(post["terms"]!.AsArray())!["name"]!.GetValue<string>();
        Assert.Equal(new string('a', AgentBlog.Posts.Taxonomy.MaxName - 1), name);
        var kept = post["excerpt"]!.GetValue<string>();
        Assert.Equal(new string('x', AgentBlog.Posts.PostService.MaxExcerpt - 2) + "…", kept);
        // Neither stored value holds half a character, so the feeds have nothing to drop.
        Assert.Null(AgentBlog.Posts.XmlText.FirstUncarryable(name));
        Assert.Null(AgentBlog.Posts.XmlText.FirstUncarryable(kept));
    }

    [Fact]
    public async Task An_old_tag_address_with_spaces_moves_though_the_request_path_arrives_decoded()
    {
        await using var m = await Move.StartAsync();
        m.Eod.Posts.Add(StubEod.Post(Rain, "space-tag", "Space tag", "Words.", "2025-03-01T09:00:00Z", null, tags: ["World Building"]));

        var job = await m.ImportAsync();
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Equal(1, job["counts"]!["imported"]!.GetValue<int>());

        // The row is stored percent-escaped; the request path arrives decoded. The old address still moves.
        var moved = await m.W.Host.Browser().Http.GetAsync("/tag/World%20Building");
        Assert.Equal(HttpStatusCode.MovedPermanently, moved.StatusCode);
        Assert.Equal("/tag/world-building", moved.Headers.Location!.OriginalString);
        Assert.Contains("Space tag", await m.PageAsync("/tag/world-building"));
    }

    [Fact]
    public async Task A_duplicate_tag_is_left_off_and_logged_as_a_duplicate_not_as_unaddressable()
    {
        await using var m = await Move.StartAsync();
        m.Eod.Posts.Add(StubEod.Post(Rain, "two-names", "Two names", "Words.", "2025-03-01T09:00:00Z", null,
            tags: ["World Building", "world_building"]));

        var job = await m.ImportAsync();
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Equal(1, job["counts"]!["imported"]!.GetValue<int>());
        Assert.Null(job["counts"]!["failed"]);
        Assert.Equal(1, job["counts"]!["terms_dropped"]!.GetValue<int>());

        // One tag at world-building; the duplicate's own log line says it is a duplicate, not that it has no address.
        var terms = (await m.W.Owner.GetJsonAsync($"/api/posts/{Rain}")).Json!["post"]!["terms"]!.AsArray();
        Assert.Equal(["World Building"], terms.Select(t => t!["name"]!.GetValue<string>()));
        var line = Assert.Single(job["log"]!.AsArray(), l => l!["text"]!.GetValue<string>().Contains("world_building"));
        Assert.Equal("warning", line!["level"]!.GetValue<string>());
        Assert.Contains("already has its address", line["text"]!.GetValue<string>());
        Assert.DoesNotContain("no A–Z/0–9", line["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_categorys_parent_is_kept_as_a_description_note_because_categories_here_are_flat()
    {
        await using var m = await Move.StartAsync();
        // The source's hierarchy: Travel (no parent) and Journal under Travel.
        m.Eod.Categories.Add(new JsonObject { ["id"] = 1, ["slug"] = "travel", ["name"] = "Travel", ["parentId"] = null });
        m.Eod.Categories.Add(new JsonObject { ["id"] = 3, ["slug"] = "journal", ["name"] = "Journal", ["parentId"] = 1 });
        m.Eod.Posts.Add(StubEod.Post(Rain, "child-post", "Child post", "Words.", "2025-03-01T09:00:00Z", null,
            category: new JsonObject { ["id"] = 3, ["slug"] = "journal", ["name"] = "Journal" }));
        m.Eod.Posts.Add(StubEod.Post(Porto, "parent-post", "Parent post", "Words.", "2025-04-01T09:00:00Z", null,
            category: new JsonObject { ["id"] = 1, ["slug"] = "travel", ["name"] = "Travel" }));

        var job = await m.ImportAsync();
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Equal(2, job["counts"]!["imported"]!.GetValue<int>());
        Assert.Null(job["counts"]!["failed"]);

        // The child keeps its parent's name as a description note (the term table has no parent column); the parent
        // itself is an ordinary category with no note.
        var blog = Guid.Parse(m.Blog);
        Assert.Contains("Travel", await m.SqlAsync<string>("SELECT description FROM term WHERE blog_id = @blog AND kind = 'category' AND slug = 'journal'", new { blog }));
        Assert.Equal("", await m.SqlAsync<string>("SELECT description FROM term WHERE blog_id = @blog AND kind = 'category' AND slug = 'travel'", new { blog }));
        // Both categories are real pages here, each with its post.
        Assert.Contains("Child post", await m.PageAsync("/category/journal"));
        Assert.Contains("Parent post", await m.PageAsync("/category/travel"));
    }

    /// <summary>A web that serves one png for any address, moves the clock past a minute at the tenth copy, and holds
    /// the eleventh fetch until released — so a mid-run save must come from the time-based heartbeat.</summary>
    private sealed class GatedWeb : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public required SubscribeTests.MovingClock Clock { get; init; }
        public void Release() => _gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n == 10) Clock.Ahead = TimeSpan.FromSeconds(61);
            if (n == 11) await _gate.Task.WaitAsync(ct);
            var content = new ByteArrayContent(MediaHost.Png);
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    /// <summary>A web where every picture copy fails (404), the tenth moves the clock past a minute, and the eleventh
    /// fetch is held until released — so a mid-run save can only come from the failing copies, not a successful one.</summary>
    private sealed class FailingGatedWeb : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public required SubscribeTests.MovingClock Clock { get; init; }
        public void Release() => _gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n == 10) Clock.Ahead = TimeSpan.FromSeconds(61);
            if (n == 11) await _gate.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task The_heartbeat_is_saved_while_pictures_copy_not_only_at_the_posts_end()
    {
        var clock = new SubscribeTests.MovingClock();
        var web = new GatedWeb { Clock = clock };
        var eod = new StubEod();
        await using var w = await PostTests.World.StartAsync(b =>
        {
            MediaHost.Configure(new FakeBucket(), web)(b);
            b.UseSetting("Blog:Import:IntervalSeconds", "0");
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock));
            b.ConfigureTestServices(s => s.AddHttpClient(ImportJobs.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => eod));
        });
        var blog = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
        var refs = Enumerable.Range(0, 12).Select(i => $"g://many/h{i}").ToList();
        eod.Posts.Add(StubEod.Post(Rain, "long-post", "Long post", string.Join("\n\n", refs.Select(r => $"![Pic]({r})")),
            "2025-03-01T09:00:00Z", null));
        foreach (var r in refs) eod.Pictures[r] = $"https://media.eod.test/signed/{r["g://many/".Length..]}.png";

        Assert.Equal(HttpStatusCode.Created, (await w.Owner.PostJsonAsync($"/api/blogs/{blog}/imports", new { source_url = StubEod.Api }, csrf: true)).Status);
        var worker = w.Host.App.Factory.Services.GetRequiredService<ImportWorker>();
        var run = Task.Run(() => worker.RunOnceAsync(default));

        // While the eleventh picture is held, the tenth picture's save must already be in the job row.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (web.Calls < 11 && DateTime.UtcNow < deadline && !run.IsCompleted) await Task.Delay(20);
        Assert.True(web.Calls >= 11, $"the run stopped early: {run.Exception?.Message}");
        await using (var conn = new NpgsqlConnection(w.Host.App.OwnerConnectionString))
        {
            var state = await conn.QuerySingleAsync<string>("SELECT state FROM import_job ORDER BY created_at DESC LIMIT 1");
            var counts = JsonNode.Parse(await conn.QuerySingleAsync<string>("SELECT counts::text FROM import_job ORDER BY created_at DESC LIMIT 1"))!;
            Assert.Equal("running", state);
            Assert.True(counts["pictures"]?.GetValue<int>() >= 10, $"mid-run counts: {counts.ToJsonString()}");
        }
        web.Release();
        Assert.True(await run);
        var job = (await w.Owner.GetJsonAsync($"/api/blogs/{blog}/imports")).Json!["imports"]![0]!;
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Equal(12, job["counts"]!["pictures"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_heartbeat_is_saved_on_failing_copies_too_not_only_successful_ones()
    {
        var clock = new SubscribeTests.MovingClock();
        var web = new FailingGatedWeb { Clock = clock };
        var eod = new StubEod();
        await using var w = await PostTests.World.StartAsync(b =>
        {
            MediaHost.Configure(new FakeBucket(), web)(b);
            b.UseSetting("Blog:Import:IntervalSeconds", "0");
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock));
            b.ConfigureTestServices(s => s.AddHttpClient(ImportJobs.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => eod));
        });
        var blog = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
        var refs = Enumerable.Range(0, 12).Select(i => $"g://many/h{i}").ToList();
        eod.Posts.Add(StubEod.Post(Rain, "long-post", "Long post", string.Join("\n\n", refs.Select(r => $"![Pic]({r})")),
            "2025-03-01T09:00:00Z", null));
        foreach (var r in refs) eod.Pictures[r] = $"https://media.eod.test/signed/{r["g://many/".Length..]}.png";

        Assert.Equal(HttpStatusCode.Created, (await w.Owner.PostJsonAsync($"/api/blogs/{blog}/imports", new { source_url = StubEod.Api }, csrf: true)).Status);
        var worker = w.Host.App.Factory.Services.GetRequiredService<ImportWorker>();
        var run = Task.Run(() => worker.RunOnceAsync(default));

        // While the eleventh copy is held, the ten failing copies (404) must already have saved a heartbeat later than
        // the claim — a timeout-heavy post never looks stale, and can't be claimed twice.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (web.Calls < 11 && DateTime.UtcNow < deadline && !run.IsCompleted) await Task.Delay(20);
        Assert.True(web.Calls >= 11, $"the run stopped early: {run.Exception?.Message}");
        await using (var conn = new NpgsqlConnection(w.Host.App.OwnerConnectionString))
        {
            var state = await conn.QuerySingleAsync<string>("SELECT state FROM import_job ORDER BY created_at DESC LIMIT 1");
            var heartbeat = await conn.QuerySingleAsync<DateTime>("SELECT heartbeat_at FROM import_job ORDER BY created_at DESC LIMIT 1");
            var started = await conn.QuerySingleAsync<DateTime>("SELECT started_at FROM import_job ORDER BY created_at DESC LIMIT 1");
            var counts = JsonNode.Parse(await conn.QuerySingleAsync<string>("SELECT counts::text FROM import_job ORDER BY created_at DESC LIMIT 1"))!;
            Assert.Equal("running", state);
            Assert.True(heartbeat > started, $"heartbeat {heartbeat:O} is not later than the claim {started:O}");
            Assert.True(counts["pictures_missing"]?.GetValue<int>() >= 10, $"mid-run counts: {counts.ToJsonString()}");
        }
        web.Release();
        Assert.True(await run);
        var job = (await w.Owner.GetJsonAsync($"/api/blogs/{blog}/imports")).Json!["imports"]![0]!;
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Equal(12, job["counts"]!["pictures_missing"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://10.1.2.3")]
    public async Task The_import_client_refuses_a_source_inside_the_network_before_connecting(string url)
    {
        // No stub: the server's own import client, the one production runs. The refusal comes before any connection,
        // so the job's log carries no internal answer.
        await using var w = await PostTests.World.StartAsync(b => b.UseSetting("Blog:Import:IntervalSeconds", "0"));
        var blog = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Created, (await w.Owner.PostJsonAsync($"/api/blogs/{blog}/imports", new { source_url = url }, csrf: true)).Status);
        Assert.True(await w.Host.App.Factory.Services.GetRequiredService<ImportWorker>().RunOnceAsync(default));
        var job = (await w.Owner.GetJsonAsync($"/api/blogs/{blog}/imports")).Json!["imports"]![0]!;
        Assert.Equal("failed", job["state"]!.GetValue<string>());
        Assert.Contains("public internet", job["error"]!.GetValue<string>());
        Assert.Null(job["counts"]!["seen"]);
    }

    [Fact]
    public async Task The_import_client_refuses_a_source_on_another_port_naming_the_port()
    {
        // No stub: the server's own import client. The port is refused at connect time, before any answer.
        await using var w = await PostTests.World.StartAsync(b => b.UseSetting("Blog:Import:IntervalSeconds", "0"));
        var blog = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Created, (await w.Owner.PostJsonAsync($"/api/blogs/{blog}/imports", new { source_url = "https://1.1.1.1:8443" }, csrf: true)).Status);
        Assert.True(await w.Host.App.Factory.Services.GetRequiredService<ImportWorker>().RunOnceAsync(default));
        var job = (await w.Owner.GetJsonAsync($"/api/blogs/{blog}/imports")).Json!["imports"]![0]!;
        Assert.Equal("failed", job["state"]!.GetValue<string>());
        Assert.Contains("port", job["error"]!.GetValue<string>());
        Assert.Contains("8443", job["error"]!.GetValue<string>());
        Assert.Null(job["counts"]!["seen"]);
    }

    [Fact]
    public async Task A_redirect_on_the_list_call_is_refused_and_no_second_request_is_made()
    {
        var eod = new StubEod { ListRedirect = "https://127.0.0.1/" };
        await using var w = await PostTests.World.StartAsync(b =>
        {
            b.UseSetting("Blog:Import:IntervalSeconds", "0");
            b.ConfigureTestServices(s => s.AddHttpClient(ImportJobs.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => eod));
        });
        var blog = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Created, (await w.Owner.PostJsonAsync($"/api/blogs/{blog}/imports", new { source_url = StubEod.Api }, csrf: true)).Status);
        Assert.True(await w.Host.App.Factory.Services.GetRequiredService<ImportWorker>().RunOnceAsync(default));
        var job = (await w.Owner.GetJsonAsync($"/api/blogs/{blog}/imports")).Json!["imports"]![0]!;
        Assert.Equal("failed", job["state"]!.GetValue<string>());
        Assert.Contains("302", job["error"]!.GetValue<string>());
        // The list call only: the redirect inside the network was never followed.
        Assert.Single(eod.Asked);
    }

    [Fact]
    public async Task The_import_clients_primary_handler_does_not_follow_redirects()
    {
        // No stub: the server's own import client registration (Program.cs), the one production runs. The stub-based
        // redirect test above replaces the primary handler, so FollowRedirects = false is never in its pipeline; this
        // resolves the real chain and walks it down to the primary, which must not auto-redirect.
        await using var w = await PostTests.World.StartAsync(b => b.UseSetting("Blog:Import:IntervalSeconds", "0"));
        // The factory's handler chain for the "import" client, exactly as production builds it (no stub in the way).
        var handlers = w.Host.App.Factory.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        var handler = handlers.CreateHandler(ImportJobs.HttpClientName);
        // Walk down through any delegating handlers (logging, etc.) to the primary, which must not auto-redirect.
        while (handler is DelegatingHandler d) handler = d.InnerHandler!;
        var primary = Assert.IsAssignableFrom<SocketsHttpHandler>(handler);
        Assert.False(primary.AllowAutoRedirect);
    }

    [Fact]
    public async Task The_owners_own_EoD_author_lands_on_the_owner_not_a_new_writer_and_their_old_address_moves()
    {
        await using var m = await Move.StartAsync();
        var ownerId = (await m.W.Owner.GetJsonAsync("/api/me")).Json!["account"]!["id"]!.GetValue<string>();
        JsonObject LenkaOnEod() => new() { ["slug"] = "lenka-eod", ["displayName"] = "Lenka" };
        m.Eod.Posts.Add(StubEod.Post(Rain, "rain-in-lisbon", "Rain in Lisbon", "It rained.", "2025-03-01T09:00:00Z", LenkaOnEod()));
        m.Eod.Posts.Add(StubEod.Post(Porto, "porto", "Porto", "Bridges.", "2025-04-02T09:00:00Z", Anna));

        // An author map can only name accounts already on this blog.
        var bad = await m.W.Owner.PostJsonAsync($"/api/blogs/{m.Blog}/imports",
            new { source_url = StubEod.Api, author_map = new Dictionary<string, string> { ["lenka-eod"] = Guid.NewGuid().ToString() } }, csrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);

        var job = await m.ImportAsync(new Dictionary<string, string> { ["lenka-eod"] = ownerId });
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Equal(2, job["counts"]!["imported"]!.GetValue<int>());
        Assert.Equal(1, job["counts"]!["authors"]!.GetValue<int>()); // Anna only: the owner is already here

        // The owner's post is written by the owner's own account; no writer account was made for them.
        var revisions = (await m.W.Owner.GetJsonAsync($"/api/posts/{Rain}")).Json!["revisions"]!.AsArray();
        Assert.Equal($"account:{ownerId}", Assert.Single(revisions)!["by"]!.GetValue<string>());
        Assert.Equal(2, await m.SqlAsync<int>("SELECT count(*)::int FROM account")); // Lenka + Anna
        Assert.Equal(1, await m.SqlAsync<int>("SELECT count(*)::int FROM blog_member WHERE role = 'writer'"));

        // The owner's EoD author address moves to their author page here.
        var moved = await m.W.Host.Browser().Http.GetAsync("/author/lenka-eod");
        Assert.Equal(HttpStatusCode.MovedPermanently, moved.StatusCode);
        Assert.Equal("/author/lenka", moved.Headers.Location!.OriginalString);

        // A later run without the map still knows that author is the owner: a new post of theirs lands on them.
        m.Eod.Posts.Add(StubEod.Post(Notes, "notes", "Notes", "More.", "2025-05-01T09:00:00Z", LenkaOnEod()));
        var again = await m.ImportAsync();
        Assert.Equal(1, again["counts"]!["imported"]!.GetValue<int>());
        Assert.Null(again["counts"]!["authors"]);
        Assert.Equal($"account:{ownerId}", Assert.Single((await m.W.Owner.GetJsonAsync($"/api/posts/{Notes}")).Json!["revisions"]!.AsArray())!["by"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_author_map_keyed_by_display_name_lands_an_author_who_has_a_slug_on_that_account()
    {
        await using var m = await Move.StartAsync();
        var ownerId = (await m.W.Owner.GetJsonAsync("/api/me")).Json!["account"]!["id"]!.GetValue<string>();
        m.Eod.Posts.Add(StubEod.Post(Rain, "rain-in-lisbon", "Rain in Lisbon", "It rained.", "2025-03-01T09:00:00Z",
            new JsonObject { ["slug"] = "lenka-eod", ["displayName"] = "Lenka" }));

        // The map is keyed by display name, but the author has a slug: the map still answers, and no writer is made.
        var job = await m.ImportAsync(new Dictionary<string, string> { ["Lenka"] = ownerId });
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Equal(1, job["counts"]!["imported"]!.GetValue<int>());
        Assert.Null(job["counts"]!["authors"]);
        Assert.Equal($"account:{ownerId}", Assert.Single((await m.W.Owner.GetJsonAsync($"/api/posts/{Rain}")).Json!["revisions"]!.AsArray())!["by"]!.GetValue<string>());
        Assert.Equal(1, await m.SqlAsync<int>("SELECT count(*)::int FROM account")); // the owner only
        Assert.Equal(0, await m.SqlAsync<int>("SELECT count(*)::int FROM blog_member WHERE role = 'writer'"));

        // Their EoD author address still moves to their page here.
        var moved = await m.W.Host.Browser().Http.GetAsync("/author/lenka-eod");
        Assert.Equal(HttpStatusCode.MovedPermanently, moved.StatusCode);
        Assert.Equal("/author/lenka", moved.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task An_author_map_keyed_by_display_name_is_remembered_under_the_authors_slug_for_a_later_run()
    {
        await using var m = await Move.StartAsync();
        var ownerId = (await m.W.Owner.GetJsonAsync("/api/me")).Json!["account"]!["id"]!.GetValue<string>();
        JsonObject LenkaOnEod() => new() { ["slug"] = "lenka-eod", ["displayName"] = "Lenka" };
        m.Eod.Posts.Add(StubEod.Post(Rain, "rain-in-lisbon", "Rain in Lisbon", "It rained.", "2025-03-01T09:00:00Z", LenkaOnEod()));

        // Run 1: the map is keyed by display name, the author has a slug. The post lands on the owner, no writer made.
        var job = await m.ImportAsync(new Dictionary<string, string> { ["Lenka"] = ownerId });
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Null(job["counts"]!["authors"]);
        Assert.Equal(0, await m.SqlAsync<int>("SELECT count(*)::int FROM blog_member WHERE role = 'writer'"));

        // Run 2: a new post by the same author, no map. The mapping is remembered under the author's slug (the key a
        // later run looks up), so the post lands on the owner again — not on a new writer nobody can claim.
        m.Eod.Posts.Add(StubEod.Post(Notes, "notes", "Notes", "More.", "2025-05-01T09:00:00Z", LenkaOnEod()));
        var again = await m.ImportAsync();
        Assert.Equal(1, again["counts"]!["imported"]!.GetValue<int>());
        Assert.Null(again["counts"]!["authors"]);
        Assert.Equal(0, await m.SqlAsync<int>("SELECT count(*)::int FROM blog_member WHERE role = 'writer'"));
        Assert.Equal($"account:{ownerId}", Assert.Single((await m.W.Owner.GetJsonAsync($"/api/posts/{Notes}")).Json!["revisions"]!.AsArray())!["by"]!.GetValue<string>());
    }
}
