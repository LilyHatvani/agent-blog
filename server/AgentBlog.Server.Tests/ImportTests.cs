using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <summary>Picture refs a signed-out reader may see, to the address of their public variant.</summary>
    public readonly Dictionary<string, string> Pictures = [];
    public HttpStatusCode? ListFails { get; set; }
    public ConcurrentBag<string> Asked { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        Asked.Add(request.Method + " " + uri.PathAndQuery);
        if (uri.AbsolutePath == "/api/blog/posts")
        {
            if (ListFails is { } failing) return new HttpResponseMessage(failing);
            var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
            int page = int.Parse(q["page"]!), size = int.Parse(q["pageSize"]!);
            var items = Posts.Skip((page - 1) * size).Take(size)
                .Select(p => new JsonObject { ["uuid"] = p["uuid"]!.DeepClone(), ["slug"] = p["slug"]!.DeepClone(), ["title"] = p["title"]!.DeepClone(), ["publishedAt"] = p["publishedAt"]!.DeepClone() });
            return Json(new JsonObject { ["items"] = new JsonArray([.. items]), ["page"] = page, ["pageSize"] = size, ["total"] = Posts.Count });
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
        public async Task<JsonNode> ImportAsync(Dictionary<string, string>? authorMap = null)
        {
            var start = await W.Owner.PostJsonAsync($"/api/blogs/{Blog}/imports",
                new { source_url = StubEod.Api + "/", profile_base = "https://chat.eod.test", author_map = authorMap }, csrf: true);
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
    public async Task A_slug_that_names_one_of_the_sites_own_pages_moves_aside_with_its_old_address()
    {
        await using var m = await Move.StartAsync();
        m.Eod.Posts.Add(StubEod.Post(Rain, "themes", "Themes", "A post named themes.", "2025-03-01T09:00:00Z", null));

        var job = await m.ImportAsync();
        Assert.Equal("done", job["state"]!.GetValue<string>());
        Assert.Equal(1, job["counts"]!["imported"]!.GetValue<int>());

        // The post moved aside; /themes is still the site's own page, never the post.
        Assert.Equal("themes-2", (await m.W.Owner.GetJsonAsync($"/api/posts/{Rain}")).Json!["post"]!["slug"]!.GetValue<string>());
        Assert.Contains("A post named themes.", await m.PageAsync("/posts/themes-2"));
        var themes = await m.W.Host.Browser().Http.GetAsync("/themes");
        Assert.DoesNotContain("A post named themes.", await themes.Content.ReadAsStringAsync());
        Assert.Equal(1, await m.SqlAsync<int>("SELECT count(*)::int FROM old_address WHERE path = '/themes' AND target = '/posts/themes-2'"));
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
}
