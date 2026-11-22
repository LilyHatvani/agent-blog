using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Categories and tags (docs/specs/taxonomy.md) and the blog's own settings, through the real host: what each level may
/// do with terms, the owner's suggestions, terms riding in revisions (restore, approvals), the reader's pages, and the
/// database refusing a term from another blog.
/// </summary>
public sealed class TaxonomyTests
{
    private static string Id(JsonNode post) => post["id"]!.GetValue<string>();
    private static string Revision(JsonNode post) => post["revision"]!.GetValue<string>();
    private static List<string> Strings(JsonNode? arr) => [.. arr!.AsArray().Select(x => x!.GetValue<string>())];

    [Fact]
    public async Task At_Propose_new_names_become_suggestions_and_the_owner_adds_or_dismisses_them()
    {
        await using var w = await PostTests.World.StartAsync();

        var post = (await w.OkAsync("create_draft", new
        {
            title = "Setting up", body_md = "Steps.", categories = new[] { "Guides" }, tags = new[] { "mcp", "Setup  Guide" },
        }))["post"]!;
        Assert.Empty(Strings(post["categories"]));
        Assert.Empty(Strings(post["tags"]));
        var suggested = post["suggested_to_owner"]!.AsArray();
        Assert.Equal(3, suggested.Count);
        Assert.Contains(suggested, s => s!["name"]!.GetValue<string>() == "Setup Guide"); // spaces tidied

        var (refused, body) = await w.ToolAsync("create_tag", new { name = "mcp" });
        Assert.True(refused);
        Assert.Equal("needs_publish", body["code"]!.GetValue<string>());

        // The owner sees them on the post, adds one and dismisses another; each is a revision by the owner.
        var view = (await w.Owner.GetJsonAsync($"/api/posts/{Id(post)}")).Json!["post"]!;
        Assert.Equal(3, view["suggested"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostJsonAsync($"/api/posts/{Id(post)}/suggestions/accept", new { kind = "tag", name = "mcp" }, csrf: true)).Status);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostJsonAsync($"/api/posts/{Id(post)}/suggestions/dismiss", new { kind = "category", name = "Guides" }, csrf: true)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.PostJsonAsync($"/api/posts/{Id(post)}/suggestions/accept", new { kind = "tag", name = "never" }, csrf: true)).Status);

        var read = await w.OkAsync("read_post", new { post = Id(post) });
        Assert.Equal(["mcp"], Strings(read["tags"]));
        Assert.Empty(Strings(read["categories"]));
        Assert.Single(read["suggested_to_owner"]!.AsArray()); // "Setup Guide" still waits
        Assert.Equal(3, read["revision_number"]!.GetValue<int>());

        // Now that "mcp" exists, the agent at Propose uses it directly.
        var second = (await w.OkAsync("create_draft", new { title = "Another", body_md = "x", tags = new[] { "MCP" } }))["post"]!;
        Assert.Equal(["mcp"], Strings(second["tags"]));
        Assert.Contains(("term_created", $"account:{(await OwnerIdAsync(w))}"), await w.EventsAsync());
    }

    [Fact]
    public async Task At_Publish_an_agent_creates_terms_and_an_edit_replaces_only_the_kinds_it_names()
    {
        await using var w = await PostTests.World.StartAsync();
        await w.SetLevelAsync("publish");

        var post = (await w.OkAsync("create_draft", new
        {
            title = "Levels", body_md = "x", categories = new[] { "Guides" }, tags = new[] { "levels", "Levels", "safety" },
        }))["post"]!;
        Assert.Equal(["Guides"], Strings(post["categories"]));
        Assert.Equal(["levels", "safety"], Strings(post["tags"])); // "Levels" is the same tag
        Assert.Empty(post["suggested_to_owner"]!.AsArray());
        Assert.Contains(("term_created", $"connection:{w.Agent.Id}"), await w.EventsAsync());

        // Tags only: the category stays. Neither: both stay.
        var edited = (await w.OkAsync("edit_post", new { post = Id(post), base_revision = Revision(post), tags = new[] { "safety" } }))["post"]!;
        Assert.Equal(["Guides"], Strings(edited["categories"]));
        Assert.Equal(["safety"], Strings(edited["tags"]));
        var again = (await w.OkAsync("edit_post", new { post = Id(post), base_revision = Revision(edited), body_md = "y" }))["post"]!;
        Assert.Equal(["Guides"], Strings(again["categories"]));
        Assert.Equal(["safety"], Strings(again["tags"]));

        var tags = (await w.OkAsync("list_tags"))["tags"]!.AsArray();
        Assert.Equal(2, tags.Count);
        Assert.Equal(1, tags.Single(t => t!["slug"]!.GetValue<string>() == "safety")!["posts"]!.GetValue<int>());
        Assert.Equal(0, tags.Single(t => t!["slug"]!.GetValue<string>() == "levels")!["posts"]!.GetValue<int>());

        var created = await w.OkAsync("create_category", new { name = "Releases", description = "What shipped." });
        Assert.Equal("releases", created["term"]!["slug"]!.GetValue<string>());

        Assert.Single((await w.OkAsync("list_posts", new { tag = "safety" }))["posts"]!.AsArray());
        Assert.Empty((await w.OkAsync("list_posts", new { tag = "levels" }))["posts"]!.AsArray());
    }

    [Fact]
    public async Task Names_sent_as_a_string_by_an_older_client_still_work_and_a_wrong_shape_says_what_is_expected()
    {
        await using var w = await PostTests.World.StartAsync();
        await w.SetLevelAsync("publish");
        var a = (await w.OkAsync("create_draft", new { title = "A", body_md = "x", categories = "[\"Guides\"]", tags = "mcp, setup" }))["post"]!;
        Assert.Equal(["Guides"], Strings(a["categories"]));
        Assert.Equal(["mcp", "setup"], Strings(a["tags"]));
        var (refused, body) = await w.ToolAsync("edit_post", new { post = Id(a), base_revision = Revision(a), tags = 42 });
        Assert.True(refused);
        Assert.Equal("invalid_argument", body["code"]!.GetValue<string>());
        Assert.Contains("list of names", body["message"]!.GetValue<string>());
        (refused, _) = await w.ToolAsync("edit_post", new { post = Id(a), base_revision = Revision(a), tags = new object[] { "ok", 1 } });
        Assert.True(refused);
    }

    [Fact]
    public async Task Names_that_cannot_be_an_address_or_too_many_are_refused_with_the_reason()
    {
        await using var w = await PostTests.World.StartAsync();
        await w.SetLevelAsync("publish");
        var (refused, body) = await w.ToolAsync("create_draft", new { title = "x", body_md = "x", tags = new[] { "日本語" } });
        Assert.True(refused);
        Assert.Contains("日本語", body["message"]!.GetValue<string>());
        (refused, body) = await w.ToolAsync("create_draft", new { title = "x", body_md = "x", tags = Enumerable.Range(1, 21).Select(i => $"t{i}").ToArray() });
        Assert.True(refused);
        Assert.Contains("at most 20 tags", body["message"]!.GetValue<string>());
        (refused, _) = await w.ToolAsync("create_draft", new { title = "x", body_md = "x", categories = Enumerable.Range(1, 6).Select(i => $"c{i}").ToArray() });
        Assert.True(refused);
        // Accents are dropped from the address, not the name.
        var post = (await w.OkAsync("create_draft", new { title = "x", body_md = "x", tags = new[] { "Čeština" } }))["post"]!;
        Assert.Equal(["Čeština"], Strings(post["tags"]));
        Assert.Equal("cestina", (await w.OkAsync("list_tags"))["tags"]![0]!["slug"]!.GetValue<string>());
    }

    [Fact]
    public async Task Terms_ride_in_revisions_restoring_an_old_one_brings_its_terms_back_and_approvals_carry_them()
    {
        await using var w = await PostTests.World.StartAsync();
        await w.SetLevelAsync("publish");
        var post = (await w.OkAsync("create_draft", new { title = "T", body_md = "x", tags = new[] { "first" } }))["post"]!;
        var second = (await w.OkAsync("edit_post", new { post = Id(post), base_revision = Revision(post), tags = new[] { "second" } }))["post"]!;
        Assert.Equal(["second"], Strings(second["tags"]));

        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostJsonAsync($"/api/posts/{Id(post)}/revisions/{Revision(post)}/restore", null, csrf: true)).Status);
        Assert.Equal(["first"], Strings((await w.OkAsync("read_post", new { post = Id(post) }))["tags"]));

        // Live, then at Propose: an edit that changes the tags waits, shows the change, and the approval applies it.
        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/posts/{Id(post)}/publish")).Status);
        await w.SetLevelAsync("propose");
        var current = await w.OkAsync("read_post", new { post = Id(post) });
        var waiting = await w.OkAsync("edit_post", new { post = Id(post), base_revision = Revision(current), tags = new[] { "second", "brand new" } });
        Assert.Equal("waiting_for_owner", waiting["status"]!.GetValue<string>());

        var request = (await w.Owner.GetJsonAsync("/api/requests")).Json!["requests"]![0]!;
        Assert.Equal(["second"], [.. request["proposal"]!["terms"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())]);
        Assert.Equal(["first"], [.. request["current"]!["terms"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())]);
        Assert.Equal("brand new", request["proposal"]!["suggested"]![0]!["name"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/requests/{request["id"]}/approve")).Status);
        var after = await w.OkAsync("read_post", new { post = Id(post) });
        Assert.Equal(["second"], Strings(after["tags"]));
        Assert.Equal("brand new", after["suggested_to_owner"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Readers_get_category_and_tag_pages_of_live_posts_only_and_they_are_in_the_sitemap()
    {
        await using var w = await PostTests.World.StartAsync();
        await w.SetLevelAsync("publish");
        var live = (await w.OkAsync("create_draft", new { title = "Live one", body_md = "x", categories = new[] { "Guides" }, tags = new[] { "mcp" } }))["post"]!;
        await w.OkAsync("publish_post", new { post = Id(live) });
        await w.OkAsync("create_draft", new { title = "Draft one", body_md = "x", tags = new[] { "mcp", "secret plans" } });

        var reader = w.Host.Browser().Http;
        var page = await (await reader.GetAsync("/posts/live-one")).Content.ReadAsStringAsync();
        Assert.Contains("href=\"https://blog.test/category/guides\"", page);
        Assert.Contains("#mcp", page);

        var tag = await reader.GetAsync("/tag/mcp");
        Assert.Equal(HttpStatusCode.OK, tag.StatusCode);
        var tagPage = await tag.Content.ReadAsStringAsync();
        Assert.Contains("Live one", tagPage);
        Assert.DoesNotContain("Draft one", tagPage);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/category/guides")).StatusCode);

        // A term only drafts carry has no page: its name doesn't leak.
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/tag/secret-plans")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/tag/nothing")).StatusCode);

        var sitemap = await (await reader.GetAsync("/sitemap.xml")).Content.ReadAsStringAsync();
        Assert.Contains("https://blog.test/tag/mcp", sitemap);
        Assert.DoesNotContain("secret-plans", sitemap);
    }

    [Fact]
    public async Task The_database_refuses_a_revision_carrying_a_term_that_is_not_its_blogs()
    {
        await using var w = await PostTests.World.StartAsync();
        await w.SetLevelAsync("publish");
        var post = (await w.OkAsync("create_draft", new { title = "T", body_md = "x" }))["post"]!;

        await using var owner = new NpgsqlConnection(w.Host.App.OwnerConnectionString);
        var other = Guid.NewGuid();
        var foreignTerm = Guid.NewGuid();
        await owner.ExecuteAsync("""
            INSERT INTO blog (id, slug, title, created_at) VALUES (@other, 'other-blog', 'Other', now());
            INSERT INTO term (id, blog_id, kind, slug, name, created_by, created_at) VALUES (@foreignTerm, @other, 'tag', 'x', 'x', 'system', now());
            """, new { other, foreignTerm });

        var ex = await Assert.ThrowsAsync<PostgresException>(() => owner.ExecuteAsync("""
            INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, excerpt, term_ids, actor, created_at)
            SELECT gen_random_uuid(), blog_id, id, 99, 'x', 'x', '', ARRAY[@foreignTerm], 'system', now() FROM post WHERE id = @post
            """, new { foreignTerm, post = Guid.Parse(Id(post)) }));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    // ------------------------------------------------------------------------------------------- blog settings

    [Fact]
    public async Task The_owner_renames_the_blog_readers_see_it_at_once_and_history_records_it()
    {
        await using var w = await PostTests.World.StartAsync();
        var blogId = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();

        var before = (await w.Owner.GetJsonAsync($"/api/blogs/{blogId}")).Json!;
        Assert.Equal("", before["description"]!.GetValue<string>());

        var saved = await w.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{blogId}", csrf: true, body: new { title = "  Agent Blog guides ", description = "How it works." });
        Assert.Equal(HttpStatusCode.OK, saved.Status);
        Assert.Equal("Agent Blog guides", saved.Json!["title"]!.GetValue<string>());

        var home = await (await w.Host.Browser().Http.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("Agent Blog guides", home);
        Assert.Contains("How it works.", home);
        Assert.Contains("Agent Blog guides", await (await w.Host.Browser().Http.GetAsync("/feed.xml")).Content.ReadAsStringAsync());
        Assert.Equal("Agent Blog guides", (await w.OkAsync("get_blog"))["blog"]!["title"]!.GetValue<string>());

        // Only what's sent changes; "" clears the description; an empty title is refused; a no-op writes nothing.
        Assert.Equal("Agent Blog guides", (await w.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{blogId}", csrf: true, body: new { description = "" })).Json!["title"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{blogId}", csrf: true, body: new { title = " " })).Status);
        var events = (await w.EventsAsync()).Count(e => e.Kind == "blog_settings_changed");
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{blogId}", csrf: true, body: new { title = "Agent Blog guides" })).Status);
        Assert.Equal(events, (await w.EventsAsync()).Count(e => e.Kind == "blog_settings_changed"));
        Assert.Equal(2, events);

        // A write needs the CSRF header; a stranger's blog reads as not found.
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{blogId}", csrf: false, body: new { title = "x" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{Guid.NewGuid()}", csrf: true, body: new { title = "x" })).Status);
    }

    private static async Task<Guid> OwnerIdAsync(PostTests.World w)
    {
        await using var conn = new NpgsqlConnection(w.Host.App.AppConnectionString);
        return await conn.QuerySingleAsync<Guid>("SELECT account_id FROM blog_member LIMIT 1");
    }
}
