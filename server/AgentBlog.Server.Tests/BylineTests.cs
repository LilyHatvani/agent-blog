using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// M2 slice 8 (docs/specs/bylines.md): every post says who wrote it and who let it out, derived from its history, on the
/// page, in the feeds and in the structured data, the same everywhere; the owner names people and agents, or turns it off.
/// </summary>
public sealed class BylineTests
{
    private sealed class Authored : IAsyncDisposable
    {
        public required MediaTests.MediaWorld M { get; init; }
        public required string Blog { get; init; }

        public PostTests.World W => M.World;

        public static async Task<Authored> StartAsync()
        {
            var m = await MediaTests.MediaWorld.StartAsync();
            var blog = (await m.World.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
            var a = new Authored { M = m, Blog = blog };
            await a.NameAgentAsync("Claude");
            return a;
        }

        public async Task NameAgentAsync(string name) => Assert.Equal(HttpStatusCode.NoContent,
            (await W.Owner.SendAsync(HttpMethod.Patch, $"/api/connections/{W.Agent.Id}", csrf: true, body: new { byline_name = name })).Status);

        public async Task<JsonResult> SettingsAsync(object patch) =>
            await W.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{Blog}", csrf: true, body: patch);

        public async Task<(string Id, string Slug, string Revision)> DraftAsync(string title, string body = "Words.")
        {
            var post = (await M.OkAsync("create_draft", new { title, body_md = body }))["post"]!;
            return (post["id"]!.GetValue<string>(), post["slug"]!.GetValue<string>(), post["revision"]!.GetValue<string>());
        }

        public async Task<string> RevisionAsync(string post) => (await M.OkAsync("read_post", new { post }))["revision"]!.GetValue<string>();

        public async Task OwnerPublishesAsync(string post) =>
            Assert.Equal(HttpStatusCode.OK, (await W.OwnerPostAsync($"/api/posts/{post}/publish")).Status);

        public async Task OwnerEditsAsync(string post, string body) => Assert.Equal(HttpStatusCode.OK,
            (await W.Owner.SendAsync(HttpMethod.Patch, $"/api/posts/{post}", csrf: true, body: new { base_revision = await RevisionAsync(post), body_md = body })).Status);

        public async Task<string> PageAsync(string slug)
        {
            var r = await M.ReaderGetAsync($"/posts/{slug}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return await r.Content.ReadAsStringAsync();
        }

        /// <summary>The byline as the page shows it, its text decoded (names are links to author pages); null when there is none.</summary>
        public async Task<string?> BylineAsync(string slug)
        {
            var m = Regex.Match(await PageAsync(slug), "<p class=\"byline\">(.*?)</p>");
            return m.Success ? WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", "")) : null;
        }

        public async Task<JsonNode> JsonLdAsync(string slug) =>
            JsonNode.Parse(Regex.Match(await PageAsync(slug), "<script type=\"application/ld\\+json\">(.*?)</script>").Groups[1].Value)!;

        public async Task<XDocument> FeedAsync(string path) => XDocument.Parse(await (await M.ReaderGetAsync(path)).Content.ReadAsStringAsync());

        public ValueTask DisposeAsync() => M.DisposeAsync();
    }

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    [Fact]
    public async Task An_agents_post_the_owner_published_says_so_on_the_page_in_the_feeds_and_to_search_engines()
    {
        await using var a = await Authored.StartAsync();
        var (id, slug, _) = await a.DraftAsync("An agent's post");
        await a.OwnerPublishesAsync(id);

        Assert.Equal("Written by Claude · approved by Lenka", await a.BylineAsync(slug));

        var ld = await a.JsonLdAsync(slug);
        var author = Assert.Single(ld["author"]!.AsArray())!;
        Assert.Equal("Person", author["@type"]!.GetValue<string>());
        Assert.Equal("Lenka", author["name"]!.GetValue<string>());
        var agent = Assert.Single(ld["contributor"]!.AsArray())!;
        Assert.Equal("SoftwareApplication", agent["@type"]!.GetValue<string>());
        Assert.Equal("Claude", agent["name"]!.GetValue<string>());

        var entry = (await a.FeedAsync("/atom.xml")).Root!.Element(Atom + "entry")!;
        Assert.Equal(["Claude"], entry.Elements(Atom + "author").Select(x => x.Element(Atom + "name")!.Value));
        Assert.Equal(["Lenka"], entry.Elements(Atom + "contributor").Select(x => x.Element(Atom + "name")!.Value));
        var item = (await a.FeedAsync("/feed.xml")).Root!.Element("channel")!.Element("item")!;
        Assert.Equal(["Claude"], item.Elements(Dc + "creator").Select(x => x.Value));

        // The app shows the owner what readers see; the agent learns its own name on posts.
        Assert.Equal("Written by Claude · approved by Lenka", (await a.W.Owner.GetJsonAsync($"/api/posts/{id}")).Json!["byline"]!["text"]!.GetValue<string>());
        Assert.Equal("Claude", (await a.M.OkAsync("get_blog"))["your_name_on_posts"]!.GetValue<string>());
    }

    [Fact]
    public async Task Who_wrote_and_who_let_it_out_come_from_the_history_alone()
    {
        await using var a = await Authored.StartAsync();

        // The owner and the agent both wrote; the owner published.
        var (both, bothSlug, _) = await a.DraftAsync("Together");
        await a.OwnerEditsAsync(both, "The owner's words.");
        await a.OwnerPublishesAsync(both);
        Assert.Equal("Written by Claude and Lenka · approved by Lenka", await a.BylineAsync(bothSlug));
        Assert.Equal(["Claude", "Lenka"], (await a.FeedAsync("/atom.xml")).Root!.Elements(Atom + "entry").First()
            .Elements(Atom + "author").Select(x => x.Element(Atom + "name")!.Value));

        // The agent published on its own.
        await a.W.SetLevelAsync("publish");
        var (alone, aloneSlug, _) = await a.DraftAsync("On its own");
        Assert.Equal("done", (await a.M.OkAsync("publish_post", new { post = alone }))["status"]!.GetValue<string>());
        Assert.Equal("Written and published by Claude", await a.BylineAsync(aloneSlug));
        var ld = await a.JsonLdAsync(aloneSlug);
        Assert.Equal("Organization", ld["author"]!["@type"]!.GetValue<string>()); // no person wrote or approved it
        Assert.Equal("Claude", ld["contributor"]![0]!["name"]!.GetValue<string>());

        // Then it changed the live post with nobody approving: said.
        Assert.Equal("done", (await a.M.OkAsync("edit_post", new { post = alone, base_revision = await a.RevisionAsync(alone), body_md = "Changed." }))["status"]!.GetValue<string>());
        Assert.Equal("Written and published by Claude · changed since by Claude", await a.BylineAsync(aloneSlug));

        // A draft shows in the app as not published yet.
        var (draft, _, _) = await a.DraftAsync("Not yet");
        Assert.Equal("Written by Claude · not published yet",
            (await a.W.Owner.GetJsonAsync($"/api/posts/{draft}")).Json!["byline"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_schedule_is_let_out_by_whoever_set_it()
    {
        await using var a = await Authored.StartAsync();
        var scheduler = a.W.Host.App.Factory.Services.GetRequiredService<AgentBlog.Posts.Scheduler>();
        var at = DateTimeOffset.UtcNow.AddMinutes(5);

        var (mine, mineSlug, _) = await a.DraftAsync("Owner's schedule");
        Assert.Equal(HttpStatusCode.OK, (await a.W.Owner.PostJsonAsync($"/api/posts/{mine}/schedule",
            new { base_revision = await a.RevisionAsync(mine), at = at.ToString("O") }, csrf: true)).Status);
        await a.W.SetLevelAsync("publish");
        var (theirs, theirsSlug, _) = await a.DraftAsync("Agent's schedule");
        Assert.Equal("done", (await a.M.OkAsync("schedule_post", new { post = theirs, at = at.ToString("O") }))["status"]!.GetValue<string>());
        await scheduler.RunOnceAsync(at.AddMinutes(1), default);

        Assert.Equal("Written by Claude · approved by Lenka", await a.BylineAsync(mineSlug));
        Assert.Equal("Written and published by Claude", await a.BylineAsync(theirsSlug));
    }

    [Fact]
    public async Task A_post_only_a_person_wrote_is_simply_by_them()
    {
        await using var a = await Authored.StartAsync();
        // The owner can't create a post through the API yet (imports will): one written by the owner goes in as an import
        // would put it, in one transaction (the keys between a post and its revision are checked at commit).
        var owner = (await a.W.Owner.GetJsonAsync("/api/me")).Json!["account"]!["id"]!.GetValue<string>();
        var id = Guid.CreateVersion7().ToString();
        const string slug = "all-mine";
        await using (var conn = new Npgsql.NpgsqlConnection(a.W.Host.App.OwnerConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var revision = Guid.CreateVersion7();
            await Dapper.SqlMapper.ExecuteAsync(conn, """
                INSERT INTO post (id, blog_id, slug, status, current_revision_id, created_at, updated_at)
                VALUES (@id, @blog, @slug, 'draft', @revision, now(), now());
                INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, excerpt, actor, created_at)
                VALUES (@revision, @blog, @id, 1, 'All mine', 'Words.', '', @actor, now());
                """, new { id = Guid.Parse(id), blog = Guid.Parse(a.Blog), slug, revision, actor = "account:" + owner }, tx);
            await tx.CommitAsync();
        }
        await a.OwnerPublishesAsync(id);
        Assert.Equal("By Lenka", await a.BylineAsync(slug));
        Assert.Equal("Lenka", (await a.JsonLdAsync(slug))["author"]![0]!["name"]!.GetValue<string>());
        Assert.Null((await a.JsonLdAsync(slug))["contributor"]);
    }

    [Fact]
    public async Task An_approved_change_after_publication_is_not_a_change_since_and_restoring_is_not_writing()
    {
        await using var a = await Authored.StartAsync();
        var (id, slug, _) = await a.DraftAsync("Approved twice");
        await a.OwnerPublishesAsync(id);

        // At Propose, the agent's edit to a live post is a request; the owner approves it.
        var ask = await a.M.OkAsync("edit_post", new { post = id, base_revision = await a.RevisionAsync(id), body_md = "Better words." });
        Assert.Equal("waiting_for_owner", ask["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await a.W.OwnerPostAsync($"/api/requests/{ask["request"]!["id"]}/approve")).Status);
        Assert.Equal("Written by Claude · approved by Lenka", await a.BylineAsync(slug));

        // Putting the first revision back is the owner's act, not their words.
        var first = (await a.W.Owner.GetJsonAsync($"/api/posts/{id}")).Json!["revisions"]!.AsArray().Last()!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await a.W.OwnerPostAsync($"/api/posts/{id}/revisions/{first}/restore")).Status);
        Assert.Equal("Written by Claude · approved by Lenka", await a.BylineAsync(slug));
    }

    [Fact]
    public async Task A_post_only_the_owner_wrote_is_by_the_owner_and_a_tag_decision_is_not_writing()
    {
        await using var a = await Authored.StartAsync();
        var (id, slug, _) = await a.DraftAsync("Mine in the end");
        // A tag the agent suggested, added by the owner: a revision, but no words.
        await a.M.OkAsync("edit_post", new { post = id, base_revision = await a.RevisionAsync(id), tags = new[] { "brand-new-tag" } });
        Assert.Equal(HttpStatusCode.OK, (await a.W.Owner.PostJsonAsync($"/api/posts/{id}/suggestions/accept", new { kind = "tag", name = "brand-new-tag" }, csrf: true)).Status);
        await a.OwnerPublishesAsync(id);
        Assert.Equal("Written by Claude · approved by Lenka", await a.BylineAsync(slug));

        // The owner's own name on posts, and an agent's, are the owner's to set.
        Assert.Equal(HttpStatusCode.OK, (await a.SettingsAsync(new { author_name = "L. H." })).Status);
        await a.NameAgentAsync("Claude (Opus)");
        Assert.Equal("Written by Claude (Opus) · approved by L. H.", await a.BylineAsync(slug));
    }

    [Fact]
    public async Task Names_are_escaped_everywhere_and_bylines_off_names_the_blog()
    {
        await using var a = await Authored.StartAsync();
        await a.NameAgentAsync("<script>alert(1)</script>");
        var (id, slug, _) = await a.DraftAsync("Escaped");
        await a.OwnerPublishesAsync(id);
        var page = await a.PageAsync(slug);
        Assert.DoesNotContain("<script>alert", page);
        Assert.Equal("Written by <script>alert(1)</script> · approved by Lenka", await a.BylineAsync(slug));
        Assert.Equal("<script>alert(1)</script>", (await a.JsonLdAsync(slug))["contributor"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("<script>alert(1)</script>", (await a.FeedAsync("/feed.xml")).Root!.Descendants(Dc + "creator").Single().Value);

        Assert.Equal(HttpStatusCode.OK, (await a.SettingsAsync(new { bylines = false })).Status);
        Assert.Null(await a.BylineAsync(slug));
        Assert.Equal("Organization", (await a.JsonLdAsync(slug))["author"]!["@type"]!.GetValue<string>());
        Assert.Null((await a.JsonLdAsync(slug))["contributor"]);
        var entry = (await a.FeedAsync("/atom.xml")).Root!.Element(Atom + "entry")!;
        Assert.Empty(entry.Elements(Atom + "contributor"));
        Assert.Empty((await a.FeedAsync("/feed.xml")).Root!.Descendants(Dc + "creator"));
        Assert.Null((await a.M.OkAsync("get_blog"))["your_name_on_posts"]);
    }

    [Fact]
    public async Task A_revoked_agent_keeps_its_name_and_names_follow_the_owners_rules()
    {
        await using var a = await Authored.StartAsync();
        var listed = (await a.W.Owner.GetJsonAsync("/api/connections")).Json!["connections"]![0]!;
        Assert.True(listed["byline_name_set"]!.GetValue<bool>());

        var (id, slug, _) = await a.DraftAsync("Before the revoke");
        await a.OwnerPublishesAsync(id);
        Assert.Equal(HttpStatusCode.NoContent, (await a.W.Owner.SendAsync(HttpMethod.Delete, $"/api/connections/{a.W.Agent.Id}", csrf: true)).Status);
        Assert.Equal("Written by Claude · approved by Lenka", await a.BylineAsync(slug));

        // Too long, or two lines: refused. Only the owner reaches this at all (session and CSRF, as every owner route).
        var other = await ConnectionTests.ConnectAsync(a.W.Owner, "Second");
        Assert.Equal(HttpStatusCode.BadRequest, (await a.W.Owner.SendAsync(HttpMethod.Patch, $"/api/connections/{other.Id}", csrf: true,
            body: new { byline_name = new string('x', 61) })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.W.Owner.SendAsync(HttpMethod.Patch, $"/api/connections/{other.Id}", csrf: true,
            body: new { byline_name = "two\nlines" })).Status);
        // Unset, the name its client gave stands in for it.
        var second = (await a.W.Owner.GetJsonAsync("/api/connections")).Json!["connections"]!.AsArray().Single(c => c!["label"]!.GetValue<string>() == "Second")!;
        Assert.False(second["byline_name_set"]!.GetValue<bool>() && second["byline_name"]!.GetValue<string>().Length == 0);
        Assert.NotEmpty(second["byline_name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await a.SettingsAsync(new { author_name = new string('y', 61) })).Status);
    }
}
