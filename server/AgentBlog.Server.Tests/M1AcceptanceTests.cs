using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// M1's acceptance boxes that are about every tool at once (docs/ROADMAP.md § M1): the tool list at every level holds
/// nothing that reaches past the blog's content; at Propose every Propose tool runs and everything above it becomes a
/// request the owner approves or declines; a connection of one blog asking for another blog's ids gets "not found"
/// from every tool; a revoked connection's next call is refused with the reason.
/// </summary>
public sealed class M1AcceptanceTests
{
    /// <summary>Every tool there is. A new tool fails this list on purpose: decide its level, then add it here.</summary>
    private static readonly string[] AllTools =
    [
        "get_blog", "list_posts", "read_post", "list_revisions", "create_draft", "edit_post", "submit_for_review",
        "publish_post", "schedule_post", "unpublish_post", "trash_post", "restore_post", "get_request",
        "upload_media", "confirm_media", "upload_media_from_url", "list_media",
        "list_categories", "list_tags", "create_category", "create_tag",
    ];

    private static string Id(JsonNode n) => n["id"]!.GetValue<string>();
    private static string Revision(JsonNode n) => n["revision"]!.GetValue<string>();

    [Theory]
    [InlineData("propose")]
    [InlineData("publish")]
    [InlineData("full")]
    public async Task At_every_level_no_tool_changes_a_level_empties_the_trash_or_touches_sign_in(string level)
    {
        await using var w = await PostTests.World.StartAsync();
        if (level != "propose") await w.SetLevelAsync(level);
        var tools = await ConnectionTests.ListToolsAsync(w.Host, w.Agent.AccessToken);
        Assert.Equal(AllTools.Order(), tools.Order());
        foreach (var t in tools)
            foreach (var word in new[] { "level", "connection", "empty", "purge", "delete", "sign", "auth", "identity", "session", "account", "settings" })
                Assert.DoesNotContain(word, t);

        // Asking for one anyway is refused, not ignored.
        var unknown = await ConnectionTests.McpAsync(w.Host, w.Agent.AccessToken, "tools/call", new { name = "set_level", arguments = new { level = "full" } });
        var text = await unknown.Content.ReadAsStringAsync();
        Assert.True(text.Contains("\"error\"") || text.Contains("\"isError\":true"), text);
        Assert.Equal(level, (await w.OkAsync("get_blog"))["your_level"]!.GetValue<string>());
    }

    [Fact]
    public async Task At_Propose_every_Propose_tool_runs_and_everything_above_it_waits_for_the_owner_who_approves_or_declines()
    {
        var bucket = new FakeBucket();
        await using var w = await PostTests.World.StartAsync(MediaHost.Configure(bucket, new FakeWeb()));

        // Propose tools run at once.
        await w.OkAsync("get_blog");
        var draft = (await w.OkAsync("create_draft", new { title = "Draft", body_md = "x" }))["post"]!;
        draft = (await w.OkAsync("edit_post", new { post = Id(draft), base_revision = Revision(draft), body_md = "y" }))["post"]!;
        await w.OkAsync("read_post", new { post = Id(draft) });
        await w.OkAsync("list_revisions", new { post = Id(draft) });
        await w.OkAsync("list_posts");
        Assert.Equal("done", (await w.OkAsync("submit_for_review", new { post = Id(draft) }))["status"]!.GetValue<string>());
        await w.OkAsync("list_categories");
        await w.OkAsync("list_tags");
        await w.OkAsync("list_media");
        var bytes = MediaHost.Png;
        await w.OkAsync("upload_media", new { content_type = "image/png", size_bytes = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) });

        // Above Propose: each becomes a request (or, for new terms, a refusal that says to suggest them).
        var live = await w.DraftAsync("Live one", "x");
        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/posts/{Id(live)}/publish")).Status);
        var trashed = await w.DraftAsync("Trashed one", "x");
        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/posts/{Id(trashed)}/trash")).Status);
        var liveNow = await w.OkAsync("read_post", new { post = Id(live) });

        var asks = new (string Tool, object Args)[]
        {
            ("publish_post", new { post = Id(draft) }),
            ("schedule_post", new { post = Id(draft), at = DateTimeOffset.UtcNow.AddDays(1).ToString("O") }),
            ("unpublish_post", new { post = Id(live) }),
            ("trash_post", new { post = Id(draft) }),
            ("restore_post", new { post = Id(trashed) }),
            ("edit_post", new { post = Id(live), base_revision = Revision(liveNow), body_md = "changed" }),
        };
        var requests = new List<string>();
        foreach (var (tool, args) in asks)
        {
            var result = await w.OkAsync(tool, args);
            Assert.True(result["status"]!.GetValue<string>() == "waiting_for_owner", $"{tool}: {result.ToJsonString()}");
            requests.Add(result["request"]!["id"]!.GetValue<string>());
        }
        foreach (var tool in new[] { "create_category", "create_tag" })
        {
            var (refused, body) = await w.ToolAsync(tool, new { name = "New" });
            Assert.True(refused, tool);
            Assert.Equal("needs_publish", body["code"]!.GetValue<string>());
        }

        // The owner sees all six and decides: approve the unpublish and the restore, decline the rest.
        var waiting = (await w.Owner.GetJsonAsync("/api/requests")).Json!["requests"]!.AsArray();
        Assert.Equal(6, waiting.Count);
        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/requests/{requests[2]}/approve")).Status);
        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/requests/{requests[4]}/approve")).Status);
        Assert.Equal("draft", (await w.OkAsync("read_post", new { post = Id(live) }))["status"]!.GetValue<string>());
        Assert.NotEqual("trash", (await w.OkAsync("read_post", new { post = Id(trashed) }))["status"]!.GetValue<string>());
        foreach (var i in new[] { 0, 1, 3, 5 })
            Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/requests/{requests[i]}/decline")).Status);
        Assert.Equal("review", (await w.OkAsync("read_post", new { post = Id(draft) }))["status"]!.GetValue<string>());
        foreach (var id in requests)
            Assert.NotEqual("waiting", (await w.OkAsync("get_request", new { request = id }))["request"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_connection_of_one_blog_asking_for_another_blogs_ids_gets_not_found_from_every_tool()
    {
        var bucket = new FakeBucket();
        await using var w = await PostTests.World.StartAsync(MediaHost.Configure(bucket, new FakeWeb()));
        await w.SetLevelAsync("full");

        // Blog B, with its own owner, connection, post, revision, request, term and picture, written straight into the
        // database: everything an agent of blog A could try to reach by id.
        var b = new
        {
            Blog = Guid.NewGuid(), Owner = Guid.NewGuid(), Connection = Guid.NewGuid(), Post = Guid.NewGuid(), Revision = Guid.NewGuid(),
            Request = Guid.NewGuid(), Term = Guid.NewGuid(), Media = Guid.NewGuid(),
        };
        await using (var owner = new NpgsqlConnection(w.Host.App.OwnerConnectionString))
        {
            await owner.ExecuteAsync("""
                INSERT INTO account (id, display_name, created_at) VALUES (@Owner, 'Someone else', now());
                INSERT INTO blog (id, slug, title, created_at) VALUES (@Blog, 'other-blog', 'Other', now());
                INSERT INTO blog_member (blog_id, account_id, created_at) VALUES (@Blog, @Owner, now());
                INSERT INTO connection (id, blog_id, label, created_by, created_at) VALUES (@Connection, @Blog, 'Their agent', @Owner, now());
                INSERT INTO term (id, blog_id, kind, slug, name, created_by, created_at) VALUES (@Term, @Blog, 'tag', 'secret', 'secret', 'system', now());
                INSERT INTO post (id, blog_id, slug, status, current_revision_id, published_at, created_at, updated_at)
                    VALUES (@Post, @Blog, 'their-post', 'live', @Revision, now(), now(), now());
                INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, term_ids, actor, created_at)
                    VALUES (@Revision, @Blog, @Post, 1, 'Theirs', 'private words', ARRAY[@Term], 'system', now());
                INSERT INTO approval_request (id, blog_id, connection_id, action, post_id, pinned_revision_id, created_at)
                    VALUES (@Request, @Blog, @Connection, 'publish', @Post, @Revision, now());
                INSERT INTO media (id, blog_id, status, content_type, size_bytes, sha256, filename, upload_expires_at, created_by, created_at)
                    VALUES (@Media, @Blog, 'pending', 'image/png', 10, decode(repeat('ab', 32), 'hex'), 'x.png', now() + interval '1 hour', 'system', now());
                """, b);
        }

        var post = b.Post.ToString();
        var attempts = new (string Tool, object Args)[]
        {
            ("read_post", new { post }), ("read_post", new { post = "their-post" }), ("list_revisions", new { post }),
            ("edit_post", new { post, base_revision = b.Revision.ToString(), body_md = "mine now" }),
            ("submit_for_review", new { post }), ("publish_post", new { post }), ("unpublish_post", new { post }),
            ("schedule_post", new { post, at = DateTimeOffset.UtcNow.AddDays(1).ToString("O") }),
            ("trash_post", new { post }), ("restore_post", new { post }),
            ("get_request", new { request = b.Request.ToString() }), ("confirm_media", new { media_id = b.Media.ToString() }),
        };
        foreach (var (tool, args) in attempts)
        {
            var (refused, body) = await w.ToolAsync(tool, args);
            Assert.True(refused, $"{tool} reached blog B: {body.ToJsonString()}");
            Assert.True(body["code"]!.GetValue<string>() == "not_found", $"{tool}: {body.ToJsonString()}");
        }

        // Lists never show B's things, and B's post is untouched.
        Assert.DoesNotContain("Theirs", (await w.OkAsync("list_posts")).ToJsonString());
        Assert.DoesNotContain("secret", (await w.OkAsync("list_tags")).ToJsonString());
        Assert.DoesNotContain("x.png", (await w.OkAsync("list_media", new { status = (string?)null })).ToJsonString());
        Assert.DoesNotContain("their-post", (await w.OkAsync("list_posts", new { tag = "secret" })).ToJsonString());
        await using var check = new NpgsqlConnection(w.Host.App.AppConnectionString);
        Assert.Equal(("live", 1), await check.QuerySingleAsync<(string, int)>(
            "SELECT p.status, r.number FROM post p JOIN post_revision r ON r.id = p.current_revision_id WHERE p.id = @Post", b));
        Assert.Equal("waiting", await check.QuerySingleAsync<string>("SELECT state FROM approval_request WHERE id = @Request", b));
    }

    [Fact]
    public async Task A_request_whose_post_moved_on_or_changed_state_does_not_apply_and_says_why()
    {
        await using var w = await PostTests.World.StartAsync();
        // The agent asks to publish; the owner publishes it themselves first.
        var draft = await w.DraftAsync("Mine", "x");
        var ask = await w.OkAsync("publish_post", new { post = Id(draft) });
        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/posts/{Id(draft)}/publish")).Status);
        var card = (await w.Owner.GetJsonAsync("/api/requests")).Json!["requests"]!.AsArray()
            .Single(r => r!["id"]!.GetValue<string>() == ask["request"]!["id"]!.GetValue<string>())!;
        Assert.False(card["still_applies"]!.GetValue<bool>());
        Assert.Contains("is live now", card["why_not"]!.GetValue<string>());
        var approve = await w.OwnerPostAsync($"/api/requests/{card["id"]}/approve");
        Assert.Equal(HttpStatusCode.Conflict, approve.Status);

        // An edit waiting for a live post; the owner trashes the post; approving the edit changes nothing.
        var post = await w.OkAsync("read_post", new { post = Id(draft) });
        var edit = await w.OkAsync("edit_post", new { post = Id(draft), base_revision = Revision(post), body_md = "into the bin?" });
        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/posts/{Id(draft)}/trash")).Status);
        var editCard = (await w.Owner.GetJsonAsync("/api/requests")).Json!["requests"]!.AsArray()
            .Single(r => r!["id"]!.GetValue<string>() == edit["request"]!["id"]!.GetValue<string>())!;
        Assert.Contains("in the trash", editCard["why_not"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await w.OwnerPostAsync($"/api/requests/{editCard["id"]}/approve")).Status);
        var after = await w.OkAsync("read_post", new { post = Id(draft) });
        Assert.Equal(1, after["revision_number"]!.GetValue<int>());
        Assert.Equal("x", after["body_md"]!.GetValue<string>());
        Assert.Equal("stale", (await w.OkAsync("get_request", new { request = editCard["id"]!.GetValue<string>() }))["request"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_revoked_connections_next_call_is_refused_mid_session_and_says_why()
    {
        await using var w = await PostTests.World.StartAsync();
        await w.OkAsync("get_blog");
        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.SendAsync(HttpMethod.Delete, $"/api/connections/{w.Agent.Id}", csrf: true)).Status);

        var r = await ConnectionTests.McpAsync(w.Host, w.Agent.AccessToken, "tools/call", new { name = "get_blog", arguments = new { } });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        var challenge = string.Join(" ", r.Headers.WwwAuthenticate.Select(h => h.ToString()));
        Assert.Contains("invalid_token", challenge);
        Assert.Contains("revoked", challenge);
    }
}
