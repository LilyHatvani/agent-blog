using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Bylines (docs/specs/bylines.md): readers see the PERSON who wrote, with the channel — an agent's words are its
/// person's, "through MCP". Every row of the table is checked on the page, in the feeds and in the structured data; the
/// agent stays a SoftwareApplication contributor in structured data only, never the visible author. With bylines off,
/// nothing changes.
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

        /// <summary>The person names the feeds credit as authors (RSS dc:creator and Atom author).</summary>
        public async Task<(string[] Rss, string[] Atom)> FeedAuthorsAsync()
        {
            var item = (await FeedAsync("/feed.xml")).Root!.Element("channel")!.Element("item")!;
            var entry = (await FeedAsync("/atom.xml")).Root!.Element(Atom + "entry")!;
            return (item.Elements(Dc + "creator").Select(x => x.Value).ToArray(),
                entry.Elements(Atom + "author").Select(x => x.Element(Atom + "name")!.Value).ToArray());
        }

        public ValueTask DisposeAsync() => M.DisposeAsync();
    }

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    private static void AssertPerson(JsonNode ld, string name)
    {
        var author = Assert.Single(ld["author"]!.AsArray())!;
        Assert.Equal("Person", author["@type"]!.GetValue<string>());
        Assert.Equal(name, author["name"]!.GetValue<string>());
    }

    private static void AssertAgentContributor(JsonNode ld, string name)
    {
        var agent = Assert.Single(ld["contributor"]!.AsArray())!;
        Assert.Equal("SoftwareApplication", agent["@type"]!.GetValue<string>());
        Assert.Equal(name, agent["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Row_owner_only_wrote_is_by_them_no_channel()
    {
        await using var a = await Authored.StartAsync();
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
        var (rss, atom) = await a.FeedAuthorsAsync();
        Assert.Equal(["Lenka"], rss);
        Assert.Equal(["Lenka"], atom);
        var ld = await a.JsonLdAsync(slug);
        AssertPerson(ld, "Lenka");
        Assert.Null(ld["contributor"]);
    }

    [Fact]
    public async Task Row_owners_agent_wrote_owner_approved_is_by_the_person_through_MCP()
    {
        await using var a = await Authored.StartAsync();
        var (id, slug, _) = await a.DraftAsync("An agent's post");
        await a.OwnerPublishesAsync(id);

        Assert.Equal("By Lenka · through MCP", await a.BylineAsync(slug));
        var (rss, atom) = await a.FeedAuthorsAsync();
        Assert.Equal(["Lenka"], rss);
        Assert.Equal(["Lenka"], atom);
        var ld = await a.JsonLdAsync(slug);
        AssertPerson(ld, "Lenka");
        AssertAgentContributor(ld, "Claude"); // the agent, in structured data only

        // The app shows the owner what readers see, with the person and the channel; the agent learns its own name.
        var app = (await a.W.Owner.GetJsonAsync($"/api/posts/{id}")).Json!["byline"]!;
        Assert.Equal("By Lenka · through MCP", app["text"]!.GetValue<string>());
        Assert.Equal("mcp", app["credits"]![0]!["via"]!.GetValue<string>());
        Assert.Equal("Claude", (await a.M.OkAsync("get_blog"))["your_name_on_posts"]!.GetValue<string>());
    }

    [Fact]
    public async Task Row_owner_wrote_part_and_agent_the_rest_is_partly_through_MCP()
    {
        await using var a = await Authored.StartAsync();
        var (id, slug, _) = await a.DraftAsync("Together");
        await a.OwnerEditsAsync(id, "The owner's words.");
        await a.OwnerPublishesAsync(id);

        Assert.Equal("By Lenka · partly through MCP", await a.BylineAsync(slug));
        var (rss, atom) = await a.FeedAuthorsAsync();
        Assert.Equal(["Lenka"], rss);
        Assert.Equal(["Lenka"], atom);
        var ld = await a.JsonLdAsync(slug);
        AssertPerson(ld, "Lenka");
        AssertAgentContributor(ld, "Claude");
    }

    [Fact]
    public async Task Row_another_persons_agent_wrote_owner_approved_names_that_person()
    {
        await using var c = await PeoplesAgentsTests.Crew.StartAsync();
        var agent = await c.ConnectAsync(c.Anna, "Anna's laptop");
        var post = await c.DraftAsync(agent, "Porto by night");
        var request = (await c.OkAsync(agent, "publish_post", new { post = post["id"]!.ToString() }))["request"]!["id"]!.GetValue<string>();
        await c.Owner.PostJsonAsync($"/api/requests/{request}/approve", null, csrf: true);

        var html = await (await c.Host.Browser().Http.GetAsync($"/posts/{post["slug"]}")).Content.ReadAsStringAsync();
        var byline = WebUtility.HtmlDecode(Regex.Replace(Regex.Match(html, "<p class=\"byline\">(.*?)</p>").Groups[1].Value, "<[^>]+>", ""));
        Assert.Equal("Written by Anna · through MCP · approved by Lenka", byline);
        // Anna links to her page, Lenka to hers; the agent's name is not on the page.
        Assert.Contains("href=\"https://blog.test/author/anna\">Anna</a>", html);
        Assert.Contains("href=\"https://blog.test/author/lenka\">Lenka</a>", html);
        Assert.DoesNotContain("claude.ai", byline); // the agent's name is in structured data only, not the byline

        var ld = JsonNode.Parse(Regex.Match(html, "<script type=\"application/ld\\+json\">(.*?)</script>").Groups[1].Value)!;
        Assert.Equal("Person", ld["author"]![0]!["@type"]!.GetValue<string>());
        Assert.Equal("Anna", ld["author"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("SoftwareApplication", ld["contributor"]![0]!["@type"]!.GetValue<string>());

        var entry = XDocument.Parse(await (await c.Host.Browser().Http.GetAsync("/atom.xml")).Content.ReadAsStringAsync()).Root!.Element(Atom + "entry")!;
        Assert.Equal(["Anna"], entry.Elements(Atom + "author").Select(x => x.Element(Atom + "name")!.Value));
        Assert.Equal(["Lenka"], entry.Elements(Atom + "contributor").Select(x => x.Element(Atom + "name")!.Value));
    }

    [Fact]
    public async Task Row_agent_published_on_its_own_is_by_the_person_published_through_MCP()
    {
        await using var a = await Authored.StartAsync();
        await a.W.SetLevelAsync("publish");
        var (id, slug, _) = await a.DraftAsync("On its own");
        Assert.Equal("done", (await a.M.OkAsync("publish_post", new { post = id }))["status"]!.GetValue<string>());

        Assert.Equal("By Lenka · published through MCP", await a.BylineAsync(slug));
        var (rss, atom) = await a.FeedAuthorsAsync();
        Assert.Equal(["Lenka"], rss);
        Assert.Equal(["Lenka"], atom);
        var ld = await a.JsonLdAsync(slug);
        AssertPerson(ld, "Lenka"); // the person behind the agent, not the blog
        AssertAgentContributor(ld, "Claude");

        // Then it changed the live post with nobody approving: said, through MCP, without the agent's name.
        Assert.Equal("done", (await a.M.OkAsync("edit_post", new { post = id, base_revision = await a.RevisionAsync(id), body_md = "Changed." }))["status"]!.GetValue<string>());
        Assert.Equal("By Lenka · published through MCP · changed since through MCP", await a.BylineAsync(slug));
    }

    [Fact]
    public async Task Row_not_live_yet_shows_the_person_and_channel_in_the_app_preview()
    {
        await using var a = await Authored.StartAsync();
        var (id, _, _) = await a.DraftAsync("Not yet");
        Assert.Equal("By Lenka · through MCP · not published yet",
            (await a.W.Owner.GetJsonAsync($"/api/posts/{id}")).Json!["byline"]!["text"]!.GetValue<string>());
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

        // The owner set the schedule: their post, through MCP. The agent set its own: published through MCP.
        Assert.Equal("By Lenka · through MCP", await a.BylineAsync(mineSlug));
        Assert.Equal("By Lenka · published through MCP", await a.BylineAsync(theirsSlug));
    }

    [Fact]
    public async Task An_approved_change_after_publication_is_not_a_change_since_and_restoring_is_not_writing()
    {
        await using var a = await Authored.StartAsync();
        var (id, slug, _) = await a.DraftAsync("Approved twice");
        await a.OwnerPublishesAsync(id);

        var ask = await a.M.OkAsync("edit_post", new { post = id, base_revision = await a.RevisionAsync(id), body_md = "Better words." });
        Assert.Equal("waiting_for_owner", ask["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await a.W.OwnerPostAsync($"/api/requests/{ask["request"]!["id"]}/approve")).Status);
        Assert.Equal("By Lenka · through MCP", await a.BylineAsync(slug));

        var first = (await a.W.Owner.GetJsonAsync($"/api/posts/{id}")).Json!["revisions"]!.AsArray().Last()!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await a.W.OwnerPostAsync($"/api/posts/{id}/revisions/{first}/restore")).Status);
        Assert.Equal("By Lenka · through MCP", await a.BylineAsync(slug));
    }

    [Fact]
    public async Task A_tag_decision_is_not_writing_and_names_follow_the_owners_rules()
    {
        await using var a = await Authored.StartAsync();
        var (id, slug, _) = await a.DraftAsync("Mine in the end");
        await a.M.OkAsync("edit_post", new { post = id, base_revision = await a.RevisionAsync(id), tags = new[] { "brand-new-tag" } });
        Assert.Equal(HttpStatusCode.OK, (await a.W.Owner.PostJsonAsync($"/api/posts/{id}/suggestions/accept", new { kind = "tag", name = "brand-new-tag" }, csrf: true)).Status);
        await a.OwnerPublishesAsync(id);
        Assert.Equal("By Lenka · through MCP", await a.BylineAsync(slug));

        // The owner's name on posts changes the person readers see; the agent's name stays in structured data only.
        Assert.Equal(HttpStatusCode.OK, (await a.SettingsAsync(new { author_name = "L. H." })).Status);
        await a.NameAgentAsync("Claude (Opus)");
        Assert.Equal("By L. H. · through MCP", await a.BylineAsync(slug));
        AssertAgentContributor(await a.JsonLdAsync(slug), "Claude (Opus)");
    }

    [Fact]
    public async Task The_agents_name_is_escaped_in_structured_data_and_never_on_the_page_and_bylines_off_names_the_blog()
    {
        await using var a = await Authored.StartAsync();
        await a.NameAgentAsync("<script>alert(1)</script>");
        var (id, slug, _) = await a.DraftAsync("Escaped");
        await a.OwnerPublishesAsync(id);
        var page = await a.PageAsync(slug);

        // The page credits the person, not the agent: the agent's name never reaches it.
        Assert.Equal("By Lenka · through MCP", await a.BylineAsync(slug));
        Assert.DoesNotContain("<script>alert", page);
        // In structured data the agent is a contributor, its name escaped so it can't close the script.
        Assert.Equal("<script>alert(1)</script>", (await a.JsonLdAsync(slug))["contributor"]![0]!["name"]!.GetValue<string>());
        Assert.DoesNotContain("<script>alert(1)</script>", Regex.Match(page, "<script type=\"application/ld\\+json\">(.*?)</script>").Groups[1].Value);

        // Bylines off: nothing changes — no byline, the blog as feed and structured-data author, no contributor.
        Assert.Equal(HttpStatusCode.OK, (await a.SettingsAsync(new { bylines = false })).Status);
        Assert.Null(await a.BylineAsync(slug));
        Assert.Equal("Organization", (await a.JsonLdAsync(slug))["author"]!["@type"]!.GetValue<string>());
        Assert.Null((await a.JsonLdAsync(slug))["contributor"]);
        var entry = (await a.FeedAsync("/atom.xml")).Root!.Element(Atom + "entry")!;
        Assert.Equal(["My blog"], entry.Elements(Atom + "author").Select(x => x.Element(Atom + "name")!.Value));
        Assert.Empty(entry.Elements(Atom + "contributor"));
        Assert.Empty((await a.FeedAsync("/feed.xml")).Root!.Descendants(Dc + "creator"));
        Assert.Null((await a.M.OkAsync("get_blog"))["your_name_on_posts"]);
    }

    [Fact]
    public async Task A_revoked_agent_keeps_its_person_on_what_it_wrote()
    {
        await using var a = await Authored.StartAsync();
        var (id, slug, _) = await a.DraftAsync("Before the revoke");
        await a.OwnerPublishesAsync(id);
        Assert.Equal(HttpStatusCode.NoContent, (await a.W.Owner.SendAsync(HttpMethod.Delete, $"/api/connections/{a.W.Agent.Id}", csrf: true)).Status);
        Assert.Equal("By Lenka · through MCP", await a.BylineAsync(slug));
        AssertAgentContributor(await a.JsonLdAsync(slug), "Claude");
    }
}
