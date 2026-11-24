using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AgentBlog.Posts;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Posts over MCP (docs/specs/posts.md) through the real host: an agent connected the way claude.ai connects, at each
/// level, against the owner's own API. What each level does at once, what waits for the owner, that what the owner
/// approves is exactly what was asked, that every save is a revision with its author, and that schedules are judged
/// when they fire.
/// </summary>
public sealed class PostTests
{
    internal static readonly StubAccount Owner = new("200000000000000001", "Lenka", null);

    /// <summary>A blog with its owner signed in, and one agent connected at Propose.</summary>
    internal sealed class World : IAsyncDisposable
    {
        public required SignInHost Host { get; init; }
        public required SignInBrowser Owner { get; init; }
        public required ConnectionTests.Connected Agent { get; init; }

        public static async Task<World> StartAsync(Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? configure = null)
        {
            var host = await SignInHost.StartAsync(configure: configure);
            var owner = host.Browser();
            await owner.SetupAsync(host.SetupToken, "discord", PostTests.Owner);
            var agent = await ConnectionTests.ConnectAsync(owner, "Claude");
            return new World { Host = host, Owner = owner, Agent = agent };
        }

        /// <summary>A tool call as the agent: whether it was refused, and its JSON.</summary>
        public async Task<(bool Refused, JsonObject Body)> ToolAsync(string tool, object? arguments = null)
        {
            var result = await ConnectionTests.RpcResultAsync(
                await ConnectionTests.McpAsync(Host, Agent.AccessToken, "tools/call", new { name = tool, arguments = arguments ?? new { } }));
            var body = JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
            return (result["isError"]?.GetValue<bool>() == true, body);
        }

        public async Task<JsonObject> OkAsync(string tool, object? arguments = null)
        {
            var (refused, body) = await ToolAsync(tool, arguments);
            Assert.False(refused, body.ToJsonString());
            return body;
        }

        public async Task SetLevelAsync(string level) => Assert.Equal(HttpStatusCode.NoContent,
            (await Owner.SendAsync(HttpMethod.Patch, $"/api/connections/{Agent.Id}", csrf: true, body: new { level, confirm_full = true })).Status);

        public async Task<JsonObject> DraftAsync(string title = "Hello", string body = "First words.")
        {
            var r = await OkAsync("create_draft", new { title, body_md = body });
            Assert.Equal("done", r["status"]!.GetValue<string>());
            return r["post"]!.AsObject();
        }

        public Task<JsonResult> OwnerPostAsync(string path) => Owner.PostJsonAsync(path, null, csrf: true);

        public async Task<List<(string Kind, string Actor)>> EventsAsync()
        {
            await using var conn = new NpgsqlConnection(Host.App.AppConnectionString);
            return [.. await conn.QueryAsync<(string, string)>("SELECT kind, actor FROM event ORDER BY id")];
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static string Id(JsonNode post) => post["id"]!.GetValue<string>();
    private static string Revision(JsonNode post) => post["revision"]!.GetValue<string>();
    private static string Status(JsonNode post) => post["status"]!.GetValue<string>();

    [Fact]
    public async Task Tool_results_carry_quotes_and_angle_brackets_as_themselves_not_escapes()
    {
        await using var w = await World.StartAsync();
        var result = await ConnectionTests.RpcResultAsync(await ConnectionTests.McpAsync(w.Host, w.Agent.AccessToken, "tools/call",
            new { name = "create_draft", arguments = new { title = "It's <fine> & `ok`", body_md = "a 'b' \"c\"" } }));
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("It's <fine> & `ok`", text); // every \u0027 an agent reads costs it context
        Assert.Contains("a 'b' \\\"c\\\"", text);
    }

    // --------------------------------------------------------------------------------------------- Propose

    [Fact]
    public async Task At_Propose_an_agent_drafts_reads_and_edits_and_every_save_is_a_revision_naming_it()
    {
        await using var w = await World.StartAsync();

        var post = await w.DraftAsync("Hello world", "First words.");
        Assert.Equal("draft", Status(post));
        Assert.Equal("hello-world", post["slug"]!.GetValue<string>());
        Assert.Equal(1, post["revision_number"]!.GetValue<int>());

        var edited = (await w.OkAsync("edit_post", new { post = "hello-world", base_revision = Revision(post), body_md = "Better words." }))["post"]!;
        Assert.Equal(2, edited["revision_number"]!.GetValue<int>());
        Assert.Equal("Hello world", edited["title"]!.GetValue<string>()); // left out, carried over

        var read = await w.OkAsync("read_post", new { post = Id(post) });
        Assert.Equal("Better words.", read["body_md"]!.GetValue<string>());

        var revisions = (await w.OkAsync("list_revisions", new { post = Id(post) }))["revisions"]!.AsArray();
        Assert.Equal(2, revisions.Count);
        Assert.All(revisions, r => Assert.Equal($"connection:{w.Agent.Id}", r!["by"]!.GetValue<string>()));

        Assert.Single((await w.OkAsync("list_posts", new { status = "draft" }))["posts"]!.AsArray());
        Assert.Single((await w.OkAsync("list_posts", new { text = "Better" }))["posts"]!.AsArray());
        Assert.Empty((await w.OkAsync("list_posts", new { status = "live" }))["posts"]!.AsArray());
    }

    [Fact]
    public async Task An_edit_against_a_stale_revision_is_refused_with_the_current_one()
    {
        await using var w = await World.StartAsync();
        var post = await w.DraftAsync();
        var first = Revision(post);
        await w.OkAsync("edit_post", new { post = Id(post), base_revision = first, title = "Second" });

        var (refused, body) = await w.ToolAsync("edit_post", new { post = Id(post), base_revision = first, title = "Lost update" });
        Assert.True(refused);
        Assert.Equal("stale_revision", body["code"]!.GetValue<string>());
        Assert.NotEqual(first, body["details"]!["current_revision"]!.GetValue<string>());
        Assert.Equal("Second", (await w.OkAsync("read_post", new { post = Id(post) }))["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task At_Propose_publishing_waits_for_the_owner_who_approves_exactly_that_revision()
    {
        await using var w = await World.StartAsync();
        var post = await w.DraftAsync();
        Assert.Equal("done", (await w.OkAsync("submit_for_review", new { post = Id(post) }))["status"]!.GetValue<string>());

        var asked = await w.OkAsync("publish_post", new { post = Id(post), base_revision = Revision(post) });
        Assert.Equal("waiting_for_owner", asked["status"]!.GetValue<string>());
        Assert.Contains("needs publish", asked["why"]!.GetValue<string>());
        var requestId = asked["request"]!["id"]!.GetValue<string>();
        Assert.Equal("review", Status(await w.OkAsync("read_post", new { post = Id(post) })));

        var waiting = Assert.Single((await w.Owner.GetJsonAsync("/api/requests")).Json!["requests"]!.AsArray())!;
        Assert.Equal("publish", waiting["action"]!.GetValue<string>());
        Assert.Equal("Claude", waiting["agent"]!.GetValue<string>());

        var approved = await w.OwnerPostAsync($"/api/requests/{requestId}/approve");
        Assert.Equal(HttpStatusCode.OK, approved.Status);
        Assert.Equal("live", Status(await w.OkAsync("read_post", new { post = Id(post) })));
        Assert.Equal("approved", (await w.OkAsync("get_request", new { request = requestId }))["request"]!["state"]!.GetValue<string>());

        // The publish is the owner's act, at the agent's request.
        var events = await w.EventsAsync();
        Assert.Contains(events, e => e.Kind == "post_published" && e.Actor.StartsWith("account:"));
        Assert.Contains(events, e => e.Kind == "approval_approved");
    }

    [Fact]
    public async Task A_request_for_a_revision_that_moved_on_turns_stale_and_changes_nothing()
    {
        await using var w = await World.StartAsync();
        var post = await w.DraftAsync();
        var asked = await w.OkAsync("publish_post", new { post = Id(post) });
        var requestId = asked["request"]!["id"]!.GetValue<string>();

        // The agent keeps writing after asking: what the owner would approve isn't what was asked.
        await w.OkAsync("edit_post", new { post = Id(post), base_revision = Revision(post), body_md = "Changed after asking." });

        var decided = await w.OwnerPostAsync($"/api/requests/{requestId}/approve");
        Assert.Equal(HttpStatusCode.Conflict, decided.Status);
        Assert.Equal("stale", decided.Json!["code"]!.GetValue<string>());
        Assert.Equal("draft", Status(await w.OkAsync("read_post", new { post = Id(post) })));
        Assert.Equal("stale", (await w.OkAsync("get_request", new { request = requestId }))["request"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_declined_request_changes_nothing()
    {
        await using var w = await World.StartAsync();
        var post = await w.DraftAsync();
        var requestId = (await w.OkAsync("publish_post", new { post = Id(post) }))["request"]!["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/requests/{requestId}/decline")).Status);
        Assert.Equal("draft", Status(await w.OkAsync("read_post", new { post = Id(post) })));
        Assert.Equal("declined", (await w.OkAsync("get_request", new { request = requestId }))["request"]!["state"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await w.OwnerPostAsync($"/api/requests/{requestId}/approve")).Status);
    }

    [Fact]
    public async Task At_Propose_an_edit_to_a_live_post_waits_and_the_approved_revision_still_names_the_agent()
    {
        await using var w = await World.StartAsync();
        var post = await w.DraftAsync();
        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/posts/{Id(post)}/publish")).Status);

        var asked = await w.OkAsync("edit_post", new { post = Id(post), base_revision = Revision(post), body_md = "Fixed a typo." });
        Assert.Equal("waiting_for_owner", asked["status"]!.GetValue<string>());
        Assert.Equal("First words.", (await w.OkAsync("read_post", new { post = Id(post) }))["body_md"]!.GetValue<string>());

        await w.OwnerPostAsync($"/api/requests/{asked["request"]!["id"]!.GetValue<string>()}/approve");
        var read = await w.OkAsync("read_post", new { post = Id(post) });
        Assert.Equal("Fixed a typo.", read["body_md"]!.GetValue<string>());
        Assert.Equal("live", Status(read));
        var newest = (await w.OkAsync("list_revisions", new { post = Id(post) }))["revisions"]![0]!;
        Assert.Equal($"connection:{w.Agent.Id}", newest["by"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------------------------ Publish and Full

    [Fact]
    public async Task At_Publish_an_agent_publishes_itself_but_trashing_still_waits()
    {
        await using var w = await World.StartAsync();
        await w.SetLevelAsync("publish");
        var post = await w.DraftAsync();

        var published = await w.OkAsync("publish_post", new { post = Id(post), base_revision = Revision(post) });
        Assert.Equal("done", published["status"]!.GetValue<string>());
        Assert.Equal("live", Status(published["post"]!));

        Assert.Equal("waiting_for_owner", (await w.OkAsync("trash_post", new { post = Id(post) }))["status"]!.GetValue<string>());
        Assert.Equal("live", Status(await w.OkAsync("read_post", new { post = Id(post) })));
    }

    [Fact]
    public async Task At_Full_an_agent_trashes_and_restores_and_nothing_is_deleted()
    {
        await using var w = await World.StartAsync();
        await w.SetLevelAsync("full");
        var post = await w.DraftAsync();
        await w.OkAsync("publish_post", new { post = Id(post) });

        Assert.Equal("trash", Status((await w.OkAsync("trash_post", new { post = Id(post) }))["post"]!));
        Assert.Single((await w.OkAsync("list_posts", new { status = "trash" }))["posts"]!.AsArray());
        var restored = (await w.OkAsync("restore_post", new { post = Id(post) }))["post"]!;
        Assert.Equal("live", Status(restored)); // back to what it was

        await using var conn = new NpgsqlConnection(w.Host.App.AppConnectionString);
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM post_revision WHERE post_id = @id::uuid", new { id = Id(post) }));
    }

    [Fact]
    public async Task The_owner_restores_an_old_revision_as_a_new_one_and_history_keeps_everything()
    {
        await using var w = await World.StartAsync();
        var post = await w.DraftAsync("Title", "Original.");
        var first = Revision(post);
        await w.OkAsync("edit_post", new { post = Id(post), base_revision = first, body_md = "The agent rewrote this." });

        var restored = await w.OwnerPostAsync($"/api/posts/{Id(post)}/revisions/{first}/restore");
        Assert.Equal(HttpStatusCode.OK, restored.Status);
        Assert.Equal(3, restored.Json!["post"]!["revision_number"]!.GetValue<int>());
        Assert.Equal("Original.", (await w.OkAsync("read_post", new { post = Id(post) }))["body_md"]!.GetValue<string>());

        var detail = (await w.Owner.GetJsonAsync($"/api/posts/{Id(post)}")).Json!;
        var by = detail["revisions"]!.AsArray().Select(r => r!["by"]!.GetValue<string>()).ToList();
        Assert.StartsWith("account:", by[0]);
        Assert.Equal($"connection:{w.Agent.Id}", by[1]);
    }

    // ---------------------------------------------------------------------------------------- schedules

    [Fact]
    public async Task A_schedule_goes_live_only_if_its_agent_can_still_publish_when_it_fires()
    {
        await using var w = await World.StartAsync();
        await w.SetLevelAsync("publish");
        var keep = await w.DraftAsync("Kept");
        var drop = await w.DraftAsync("Dropped");
        var at = DateTimeOffset.UtcNow.AddHours(1);
        Assert.Equal("scheduled", Status((await w.OkAsync("schedule_post", new { post = Id(keep), at = at.ToString("O") }))["post"]!));
        await w.OkAsync("schedule_post", new { post = Id(drop), at = at.AddMinutes(1).ToString("O") });

        var scheduler = w.Host.App.Factory.Services.GetRequiredService<Scheduler>();
        Assert.Empty(await scheduler.RunOnceAsync(DateTimeOffset.UtcNow, default)); // not due yet

        // The first comes due while the agent can still publish; then the owner lowers it, and the second comes due.
        var first = await scheduler.RunOnceAsync(at.AddSeconds(30), default);
        Assert.Equal("published", Assert.Single(first).Outcome);
        await w.SetLevelAsync("propose");
        var second = await scheduler.RunOnceAsync(at.AddMinutes(2), default);
        var dropped = Assert.Single(second);
        Assert.Equal("dropped", dropped.Outcome);
        Assert.Contains("now at propose", dropped.Reason);

        Assert.Equal("live", Status(await w.OkAsync("read_post", new { post = Id(keep) })));
        Assert.Equal("draft", Status(await w.OkAsync("read_post", new { post = Id(drop) })));
        Assert.Contains(await w.EventsAsync(), e => e.Kind == "schedule_dropped");
    }

    [Fact]
    public async Task A_revoked_agents_schedule_never_fires()
    {
        await using var w = await World.StartAsync();
        await w.SetLevelAsync("publish");
        var post = await w.DraftAsync();
        var at = DateTimeOffset.UtcNow.AddHours(1);
        await w.OkAsync("schedule_post", new { post = Id(post), at = at.ToString("O") });

        await w.Owner.SendAsync(HttpMethod.Delete, $"/api/connections/{w.Agent.Id}", csrf: true);
        var fired = Assert.Single(await w.Host.App.Factory.Services.GetRequiredService<Scheduler>().RunOnceAsync(at.AddMinutes(1), default));
        Assert.Equal("dropped", fired.Outcome);
        Assert.Contains("revoked", fired.Reason);
    }

    // ------------------------------------------------------------------------------------------ edges

    [Fact]
    public async Task Another_blogs_post_reads_as_not_found_and_cannot_be_changed()
    {
        await using var w = await World.StartAsync();
        var theirs = Guid.CreateVersion7();
        await using (var conn = new NpgsqlConnection(w.Host.App.AppConnectionString))
        {
            var blog = Guid.CreateVersion7();
            var revision = Guid.CreateVersion7();
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("""
                INSERT INTO blog (id, slug, title, created_at) VALUES (@blog, 'other', 'Other', now());
                INSERT INTO post (id, blog_id, slug, current_revision_id, created_at, updated_at) VALUES (@theirs, @blog, 'secret', @revision, now(), now());
                INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, actor, created_at)
                VALUES (@revision, @blog, @theirs, 1, 'Secret', 'Not yours.', 'system', now());
                """, new { blog, theirs, revision }, tx);
            await tx.CommitAsync();
        }

        foreach (var (tool, args) in new (string, object)[]
                 {
                     ("read_post", new { post = theirs.ToString() }),
                     ("read_post", new { post = "secret" }),
                     ("list_revisions", new { post = theirs.ToString() }),
                     ("edit_post", new { post = theirs.ToString(), base_revision = Guid.NewGuid().ToString(), title = "Mine now" }),
                     ("submit_for_review", new { post = theirs.ToString() }),
                 })
        {
            var (refused, body) = await w.ToolAsync(tool, args);
            Assert.True(refused, tool);
            Assert.Equal("not_found", body["code"]!.GetValue<string>());
        }
        Assert.Empty((await w.OkAsync("list_posts"))["posts"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.GetJsonAsync($"/api/posts/{theirs}")).Status);
    }

    [Fact]
    public async Task Bad_input_is_refused_with_its_reason()
    {
        await using var w = await World.StartAsync();
        var (refused, body) = await w.ToolAsync("create_draft", new { title = "", body_md = "x" });
        Assert.True(refused);
        Assert.Equal("invalid_argument", body["code"]!.GetValue<string>());

        var post = await w.DraftAsync();
        await w.SetLevelAsync("publish");
        var (past, pastBody) = await w.ToolAsync("schedule_post", new { post = Id(post), at = "2020-01-01T00:00:00Z" });
        Assert.True(past);
        Assert.Contains("future", pastBody["message"]!.GetValue<string>());
        var (twice, twiceBody) = await w.ToolAsync("restore_post", new { post = Id(post) });
        Assert.True(twice);
        Assert.Equal("invalid_state", twiceBody["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_title_with_a_control_character_is_refused_with_the_reason()
    {
        await using var w = await World.StartAsync();
        var bad = "Bad" + (char)1 + "title"; // U+0001: the XML feeds can't carry it
        var (refused, body) = await w.ToolAsync("create_draft", new { title = bad, body_md = "x" });
        Assert.True(refused);
        Assert.Equal("invalid_argument", body["code"]!.GetValue<string>());
        Assert.Contains("U+0001, a character XML can't carry", body["message"]!.GetValue<string>());

        var post = await w.DraftAsync();
        var (edited, editBody) = await w.ToolAsync("edit_post", new { post = Id(post), base_revision = Revision(post), title = bad });
        Assert.True(edited);
        Assert.Equal("invalid_argument", editBody["code"]!.GetValue<string>());
        Assert.Contains("U+0001, a character XML can't carry", editBody["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_title_is_one_line_and_keeps_every_other_character_XML_carries()
    {
        await using var w = await World.StartAsync();
        var post = await w.DraftAsync();

        // A tab or a line break: refused because a title is one line (XML itself carries them), on create and on edit.
        foreach (var bad in new[] { "Two\tparts", "Two\nlines", "Two\rlines", "Two\r\nlines", "Trailing\n" })
        {
            var (refused, body) = await w.ToolAsync("create_draft", new { title = bad, body_md = "x" });
            Assert.True(refused);
            Assert.Equal("invalid_argument", body["code"]!.GetValue<string>());
            Assert.Equal("title is one line: take out the tab or line break.", body["message"]!.GetValue<string>());
            var (edited, editBody) = await w.ToolAsync("edit_post", new { post = Id(post), base_revision = Revision(post), title = bad });
            Assert.True(edited);
            Assert.Equal("title is one line: take out the tab or line break.", editBody["message"]!.GetValue<string>());
        }
        // The owner's own API answers the same.
        var blog = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
        var ownerRefused = await w.Owner.PostJsonAsync($"/api/blogs/{blog}/posts", new { title = "Two\nlines", body_md = "x" }, csrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, ownerRefused.Status);

        // Everything else XML carries is a title's to keep, and reaches the feeds whole: a right-to-left mark, an emoji
        // ZWJ sequence.
        var rlm = "Shalom " + (char)0x200F + "world";
        var family = "Family " + char.ConvertFromUtf32(0x1F468) + (char)0x200D + char.ConvertFromUtf32(0x1F469) + (char)0x200D
            + char.ConvertFromUtf32(0x1F467);
        foreach (var good in new[] { rlm, family })
        {
            var made = await w.DraftAsync(good);
            Assert.Equal(good, made["title"]!.GetValue<string>());
            Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostJsonAsync($"/api/posts/{Id(made)}/publish",
                new { base_revision = Revision(made) }, csrf: true)).Status);
        }
        var rss = XDocument.Parse(await (await w.Host.Browser().Http.GetAsync("/feed.xml")).Content.ReadAsStringAsync());
        Assert.Equal([family, rlm], rss.Descendants("item").Select(i => i.Element("title")!.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_title_stored_before_the_one_line_rule_never_blocks_an_edit_to_the_body()
    {
        await using var w = await World.StartAsync();
        var post = await w.DraftAsync("Old title");
        // A revision from before the rule (or restored from an export) whose title carries a tab.
        var revision = Guid.CreateVersion7();
        await using (var conn = new NpgsqlConnection(w.Host.App.OwnerConnectionString))
        {
            await conn.ExecuteAsync("""
                INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, excerpt, actor, created_at, term_ids)
                SELECT @revision, blog_id, post_id, number + 1, 'Old' || chr(9) || 'title', body_md, excerpt, actor, now(), term_ids
                FROM post_revision WHERE id = (SELECT current_revision_id FROM post WHERE id = @id::uuid);
                UPDATE post SET current_revision_id = @revision WHERE id = @id::uuid;
                """, new { revision, id = Id(post) });
        }

        var edited = await w.OkAsync("edit_post", new { post = Id(post), base_revision = revision.ToString(), body_md = "New words." });
        Assert.Equal("done", edited["status"]!.GetValue<string>());
        Assert.Equal("Old\ttitle", edited["post"]!["title"]!.GetValue<string>());
        // Changing the title holds it to the rule.
        var (refused, body) = await w.ToolAsync("edit_post", new
        {
            post = Id(post), base_revision = edited["post"]!["revision"]!.GetValue<string>(), title = "Still\ttabbed",
        });
        Assert.True(refused);
        Assert.Equal("title is one line: take out the tab or line break.", body["message"]!.GetValue<string>());
    }

    [Fact]
    public void Slugs_are_plain_ascii()
    {
        Assert.Equal("cesky-text-a-tak", PostService.Slugify("Český text — a tak!"));
        Assert.Equal("post", PostService.Slugify("—"));
    }
}
