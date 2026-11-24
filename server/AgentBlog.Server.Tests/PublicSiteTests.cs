using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AgentBlog.Public;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// What readers see (docs/specs/public-site.md), through the real host: only live posts exist, an agent's Markdown
/// can't run script in a reader's browser, the feeds are valid, and paging, SEO tags and hosted subdomains work.
/// </summary>
public sealed class PublicSiteTests
{
    private static string Id(JsonNode post) => post["id"]!.GetValue<string>();

    /// <summary>A draft by the agent, published by the owner. Returns its slug.</summary>
    private static async Task<string> PublishedAsync(PostTests.World w, string title, string body, string? excerpt = null)
    {
        var r = await w.OkAsync("create_draft", new { title, body_md = body, excerpt });
        var post = r["post"]!;
        var published = await w.OwnerPostAsync($"/api/posts/{Id(post)}/publish");
        Assert.True(published.Status == HttpStatusCode.OK, $"{published.Status} {published.Json?.ToJsonString()} {r.ToJsonString()}");
        return post["slug"]!.GetValue<string>();
    }

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> GetAsync(PostTests.World w, string path, string? host = null)
    {
        var http = w.Host.Browser().Http; // a reader: no cookies
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (host is not null) request.Headers.Host = host;
        var r = await http.SendAsync(request);
        return (r.StatusCode, await r.Content.ReadAsStringAsync(), r);
    }

    [Fact]
    public async Task Only_live_posts_exist_for_readers()
    {
        await using var w = await PostTests.World.StartAsync();
        var live = await PublishedAsync(w, "Live one", "Hello readers.", "A short summary.");
        var draft = (await w.OkAsync("create_draft", new { title = "Secret draft", body_md = "Not yet." }))["post"]!;
        var trashed = await PublishedAsync(w, "Trashed one", "Gone.");
        var trashedId = (await w.OkAsync("read_post", new { post = trashed }))["id"]!.GetValue<string>();
        await w.OwnerPostAsync($"/api/posts/{trashedId}/trash");

        var home = await GetAsync(w, "/");
        Assert.Equal(HttpStatusCode.OK, home.Status);
        Assert.Contains("Live one", home.Body);
        Assert.Contains("A short summary.", home.Body);
        Assert.DoesNotContain("Secret draft", home.Body);
        Assert.DoesNotContain("Trashed one", home.Body);

        var page = await GetAsync(w, $"/posts/{live}");
        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("<p>Hello readers.</p>", page.Body);

        foreach (var hidden in new[] { draft["slug"]!.GetValue<string>(), trashed, "never-existed" })
        {
            var r = await GetAsync(w, $"/posts/{hidden}");
            Assert.Equal(HttpStatusCode.NotFound, r.Status);
            Assert.Contains("noindex", r.Body);
            Assert.DoesNotContain("Not yet.", r.Body);
        }
    }

    [Fact]
    public async Task An_agents_Markdown_cannot_run_script_in_a_readers_browser()
    {
        await using var w = await PostTests.World.StartAsync();
        var slug = await PublishedAsync(w, "Title <b>bold</b> <script>alert(1)</script>", """
            <script>alert('raw')</script>
            <img src=x onerror="alert('img')">

            [click](javascript:alert('link')) and [fine](https://example.com) and [rel](/posts/other)

            ![pic](javascript:alert('pic'))

            <https://example.org>
            """);

        var page = await GetAsync(w, $"/posts/{slug}");
        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.DoesNotContain("<script>", page.Body);
        Assert.DoesNotContain("onerror=\"", page.Body);
        Assert.DoesNotContain("javascript:", page.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>bold</b>", page.Body);
        Assert.Contains("&lt;script&gt;", page.Body);
        Assert.Contains("href=\"https://example.com\"", page.Body);
        Assert.Contains("href=\"/posts/other\"", page.Body);
        Assert.Contains("script-src", page.Response.Headers.GetValues("Content-Security-Policy").Single() + "script-src");
        Assert.DoesNotContain("'unsafe-inline'", page.Response.Headers.GetValues("Content-Security-Policy").Single());

        // The feeds carry the same sanitized HTML.
        var rss = await GetAsync(w, "/feed.xml");
        Assert.DoesNotContain("javascript:", rss.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>alert", rss.Body);
    }

    [Fact]
    public async Task The_feeds_and_sitemap_are_valid_and_list_only_live_posts()
    {
        await using var w = await PostTests.World.StartAsync();
        var slug = await PublishedAsync(w, "Feed me", "Body **bold**.", "Summary.");
        await w.OkAsync("create_draft", new { title = "Not in feeds", body_md = "x" });
        var url = $"{SignInHost.PublicUrl}/posts/{slug}";

        var rss = XDocument.Parse((await GetAsync(w, "/feed.xml")).Body);
        var item = Assert.Single(rss.Descendants("item"));
        Assert.Equal("Feed me", item.Element("title")!.Value);
        Assert.Equal(url, item.Element("link")!.Value);
        Assert.Contains("<strong>bold</strong>", item.Element(XName.Get("encoded", "http://purl.org/rss/1.0/modules/content/"))!.Value);
        // The guid is still the post's address (a reader keys on it: changing it would show the post again); the post's
        // uuid rides beside it, as Dublin Core's identifier.
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        var uuid = "urn:uuid:" + (await w.OkAsync("read_post", new { post = slug }))["id"]!.GetValue<string>();
        Assert.Equal(url, item.Element("guid")!.Value);
        Assert.Equal("true", item.Element("guid")!.Attribute("isPermaLink")!.Value);
        Assert.Equal(uuid, Assert.Single(item.Elements(dc + "identifier")).Value);

        XNamespace atom = "http://www.w3.org/2005/Atom";
        var feed = XDocument.Parse((await GetAsync(w, "/atom.xml")).Body);
        var entry = Assert.Single(feed.Descendants(atom + "entry"));
        Assert.Equal(url, entry.Element(atom + "id")!.Value);
        Assert.Single(entry.Elements(atom + "id"));
        Assert.Equal(uuid, Assert.Single(entry.Elements(dc + "identifier")).Value);

        XNamespace sm = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var locs = XDocument.Parse((await GetAsync(w, "/sitemap.xml")).Body).Descendants(sm + "loc").Select(l => l.Value).ToList();
        Assert.Equal([SignInHost.PublicUrl + "/", url], locs);

        var robots = await GetAsync(w, "/robots.txt");
        Assert.Contains($"Sitemap: {SignInHost.PublicUrl}/sitemap.xml", robots.Body);
        Assert.Contains("Disallow: /admin", robots.Body);
    }

    [Fact]
    public async Task A_live_post_answers_by_its_uuid_and_every_other_id_gets_the_same_not_found()
    {
        await using var w = await PostTests.World.StartAsync();
        await w.SetLevelAsync("full");
        var liveSlug = await PublishedAsync(w, "Out now", "Readers can see this.");
        var live = await w.OkAsync("read_post", new { post = liveSlug });

        // Every state a post can be in but live, each named by its uuid.
        var draft = Id((await w.OkAsync("create_draft", new { title = "Secret draft", body_md = "Not yet." }))["post"]!);
        var review = Id((await w.OkAsync("create_draft", new { title = "Secret review", body_md = "Not yet." }))["post"]!);
        await w.OkAsync("submit_for_review", new { post = review });
        var scheduled = Id((await w.OkAsync("create_draft", new { title = "Secret schedule", body_md = "Not yet." }))["post"]!);
        Assert.Equal("scheduled", (await w.OkAsync("schedule_post", new { post = scheduled, at = DateTimeOffset.UtcNow.AddHours(1).ToString("O") }))["post"]!["status"]!.GetValue<string>());
        var trashedSlug = await PublishedAsync(w, "Secret trashed", "Gone.");
        var trashed = Id(await w.OkAsync("read_post", new { post = trashedSlug }));
        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/posts/{trashed}/trash")).Status);
        // A live post on another blog of the instance: not this blog's.
        var elsewhere = Guid.CreateVersion7();
        await using (var conn = new NpgsqlConnection(w.Host.App.AppConnectionString))
        {
            var blog = Guid.CreateVersion7();
            var revision = Guid.CreateVersion7();
            await conn.ExecuteAsync("""
                INSERT INTO blog (id, slug, title, created_at) VALUES (@blog, 'other', 'Other', now());
                INSERT INTO post (id, blog_id, slug, status, current_revision_id, published_at, created_at, updated_at)
                VALUES (@elsewhere, @blog, 'secret-elsewhere', 'live', @revision, now(), now(), now());
                INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, actor, created_at)
                VALUES (@revision, @blog, @elsewhere, 1, 'Secret elsewhere', 'Not this blog''s.', 'system', now());
                """, new { blog, elsewhere, revision });
        }

        var found = await GetAsync(w, $"/api/public/posts/{Id(live)}");
        Assert.Equal(HttpStatusCode.OK, found.Status);
        var facts = JsonNode.Parse(found.Body)!;
        Assert.Equal(Id(live), facts["id"]!.GetValue<string>());
        Assert.Equal("Out now", facts["title"]!.GetValue<string>());
        Assert.Equal($"{SignInHost.PublicUrl}/posts/{liveSlug}", facts["url"]!.GetValue<string>());
        var published = DateTimeOffset.Parse(facts["published_at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(published, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(["id", "published_at", "title", "url"], facts.AsObject().Select(p => p.Key).Order());
        Assert.Equal("public, max-age=60", found.Response.Headers.CacheControl!.ToString());
        // Any form .NET reads as that uuid (upper case, no hyphens) is the same post.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(w, $"/api/public/posts/{Guid.Parse(Id(live)):N}")).Status);

        // The miss every other answer must equal, byte for byte and header for header.
        var unknown = await GetAsync(w, $"/api/public/posts/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal("application/problem+json", unknown.Response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", unknown.Response.Headers.CacheControl!.ToString());
        foreach (var hidden in new[] { draft, review, scheduled, trashed, elsewhere.ToString(), "not-a-uuid", "secret-elsewhere", liveSlug, "00000000-0000-0000-0000-000000000000" })
        {
            var r = await GetAsync(w, $"/api/public/posts/{hidden}");
            Assert.Equal(HttpStatusCode.NotFound, r.Status);
            Assert.Equal(unknown.Body, r.Body);
            Assert.Equal(Headers(unknown.Response), Headers(r.Response));
            Assert.DoesNotContain("Secret", r.Body);
        }

        // The feeds stay what they were: only the live post, with its uuid; nothing of the others, by title or by id.
        foreach (var path in new[] { "/feed.xml", "/atom.xml" })
        {
            var feed = (await GetAsync(w, path)).Body;
            Assert.Contains($"urn:uuid:{Id(live)}", feed);
            foreach (var hidden in new[] { draft, review, scheduled, trashed, elsewhere.ToString() }) Assert.DoesNotContain(hidden, feed);
            Assert.DoesNotContain("Secret", feed);
        }

        // Once the post leaves (into the trash), its uuid reads like any other miss.
        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/posts/{Id(live)}/trash")).Status);
        var gone = await GetAsync(w, $"/api/public/posts/{Id(live)}");
        Assert.Equal(HttpStatusCode.NotFound, gone.Status);
        Assert.Equal(unknown.Body, gone.Body);
    }

    [Fact]
    public async Task A_post_by_its_uuid_on_a_hidden_blog_gets_the_same_not_found()
    {
        await using var w = await PostTests.World.StartAsync();
        var slug = await PublishedAsync(w, "Was public", "x");
        var id = Id(await w.OkAsync("read_post", new { post = slug }));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(w, $"/api/public/posts/{id}")).Status);
        var unknown = await GetAsync(w, $"/api/public/posts/{Guid.NewGuid()}");
        await using (var conn = new NpgsqlConnection(w.Host.App.AppConnectionString))
            await conn.ExecuteAsync("UPDATE blog SET hidden_at = now()");

        var hidden = await GetAsync(w, $"/api/public/posts/{id}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.Status);
        Assert.Equal(unknown.Body, hidden.Body);
        Assert.Equal(Headers(unknown.Response), Headers(hidden.Response));
    }

    /// <summary>Every header a response carries, content headers included, but the Date: what two misses must share.</summary>
    private static List<string> Headers(HttpResponseMessage r) =>
        [.. r.Headers.Concat(r.Content.Headers).Where(h => h.Key != "Date").Select(h => $"{h.Key}: {string.Join(", ", h.Value)}").Order(StringComparer.Ordinal)];

    [Fact]
    public async Task A_post_page_carries_its_canonical_and_link_preview_tags()
    {
        await using var w = await PostTests.World.StartAsync();
        var withExcerpt = await PublishedAsync(w, "Tagged", "Long body here.", "The excerpt.");
        var withoutExcerpt = await PublishedAsync(w, "Untagged", "First sentence of the body. More.");

        var page = (await GetAsync(w, $"/posts/{withExcerpt}")).Body;
        Assert.Contains($"<link rel=\"canonical\" href=\"{SignInHost.PublicUrl}/posts/{withExcerpt}\">", page);
        Assert.Contains("<meta property=\"og:type\" content=\"article\">", page);
        Assert.Contains("<meta name=\"description\" content=\"The excerpt.\">", page);
        Assert.Contains("article:published_time", page);
        Assert.Contains("application/rss+xml", page);

        Assert.Contains("content=\"First sentence of the body. More.\"", (await GetAsync(w, $"/posts/{withoutExcerpt}")).Body);
    }

    [Fact]
    public async Task The_home_page_pages_by_ten_newest_first()
    {
        await using var w = await PostTests.World.StartAsync();
        for (var i = 1; i <= 12; i++) await PublishedAsync(w, $"Post number {i:00}", "x");

        var first = (await GetAsync(w, "/")).Body;
        Assert.Contains("Post number 12", first);
        Assert.DoesNotContain("Post number 02", first);
        Assert.Contains("href=\"/?page=2\"", first);
        Assert.DoesNotContain("rel=\"prev\"", first);

        var second = (await GetAsync(w, "/?page=2")).Body;
        Assert.Contains("Post number 01", second);
        Assert.Contains("Post number 02", second);
        Assert.DoesNotContain("Post number 03", second);
        Assert.Contains("rel=\"prev\" href=\"/\"", second);
        Assert.DoesNotContain("rel=\"next\"", second);
    }

    [Fact]
    public async Task A_hidden_blog_answers_not_found()
    {
        await using var w = await PostTests.World.StartAsync();
        var slug = await PublishedAsync(w, "Was public", "x");
        await using (var conn = new NpgsqlConnection(w.Host.App.AppConnectionString))
            await conn.ExecuteAsync("UPDATE blog SET hidden_at = now()");

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(w, "/")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(w, $"/posts/{slug}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(w, "/feed.xml")).Status);
    }

    [Fact]
    public async Task Hosted_each_blog_answers_on_its_own_subdomain_only()
    {
        await using var w = await PostTests.World.StartAsync(b => b.UseSetting("Blog:Hosting:Domain", "blogs.test"));
        var slug = await PublishedAsync(w, "Hosted post", "x");

        var mine = await GetAsync(w, "/", host: "test-blog.blogs.test");
        Assert.Equal(HttpStatusCode.OK, mine.Status);
        Assert.Contains("Hosted post", mine.Body);
        Assert.Contains($"href=\"https://test-blog.blogs.test/posts/{slug}\"", mine.Body);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(w, "/", host: "someone-else.blogs.test")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(w, "/", host: "a.b.blogs.test")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(w, "/", host: "elsewhere.test")).Status);
    }

    [Fact]
    public async Task Unknown_reader_paths_get_the_themes_not_found_page_and_the_api_keeps_its_own()
    {
        await using var w = await PostTests.World.StartAsync();
        var page = await GetAsync(w, "/no/such/page");
        Assert.Equal(HttpStatusCode.NotFound, page.Status);
        Assert.Contains("There's no page at this address.", page.Body);

        var api = await GetAsync(w, "/api/nothing-here");
        Assert.Equal(HttpStatusCode.NotFound, api.Status);
        Assert.DoesNotContain("<!doctype", api.Body);
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("mailto:me@example.com", true)]
    [InlineData("/posts/x", true)]
    [InlineData("#top", true)]
    [InlineData("relative/path:with-colon", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JavaScript:alert(1)", false)]
    [InlineData(" java\tscript:alert(1)", false)]
    [InlineData("data:text/html,<script>", false)]
    [InlineData("vbscript:x", false)]
    public void Only_safe_link_schemes_survive(string url, bool safe) => Assert.Equal(safe, Markdown.SafeUrl(url));
}
