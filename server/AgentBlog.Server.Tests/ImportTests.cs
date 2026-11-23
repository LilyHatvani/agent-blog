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
            var refs = body["trees"]![0]!["refs"]!.AsArray().Select(r => r!.GetValue<string>());
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
        public async Task<JsonNode> ImportAsync()
        {
            var start = await W.Owner.PostJsonAsync($"/api/blogs/{Blog}/imports",
                new { source_url = StubEod.Api + "/", profile_base = "https://chat.eod.test" }, csrf: true);
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
        Assert.True(rainPage.Contains("By <a rel=\"author\" href=\"https://blog.test/author/anna\">Anna Nováková</a>"),
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
}
