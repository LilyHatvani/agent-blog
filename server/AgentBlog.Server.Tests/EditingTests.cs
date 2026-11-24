using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Write together, slice 2 (docs/specs/editing.md): the owner edits a post in the app under the same revision rules as
/// agents, and approves a request with their own edits on top, each author keeping their words.
/// </summary>
public sealed class EditingTests
{
    private static string Id(JsonNode n) => n["id"]!.GetValue<string>();
    private static string Revision(JsonNode n) => n["revision"]!.GetValue<string>();

    private static async Task<JsonArray> RevisionsAsync(PostTests.World w, string post) =>
        (await w.Owner.GetJsonAsync($"/api/posts/{post}")).Json!["revisions"]!.AsArray();

    [Fact]
    public async Task The_owner_edits_a_post_as_a_revision_of_their_own_against_the_one_they_read()
    {
        await using var w = await PostTests.World.StartAsync();
        var draft = await w.DraftAsync("Draft", "One sentence is wrong.");

        var saved = await w.Owner.SendAsync(HttpMethod.Patch, $"/api/posts/{Id(draft)}", csrf: true,
            body: new { base_revision = Revision(draft), body_md = "One sentence is right now." });
        Assert.Equal(HttpStatusCode.OK, saved.Status);
        var read = await w.OkAsync("read_post", new { post = Id(draft) });
        Assert.Equal("One sentence is right now.", read["body_md"]!.GetValue<string>());
        Assert.Equal("Draft", read["title"]!.GetValue<string>()); // left out, kept
        Assert.Equal("Lenka", (await RevisionsAsync(w, Id(draft)))[0]!["by_label"]!.GetValue<string>());

        // The revision it was read at moved on: refused, nothing overwritten.
        var stale = await w.Owner.SendAsync(HttpMethod.Patch, $"/api/posts/{Id(draft)}", csrf: true,
            body: new { base_revision = Revision(draft), body_md = "Overwrite?" });
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("One sentence is right now.", (await w.OkAsync("read_post", new { post = Id(draft) }))["body_md"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.SendAsync(HttpMethod.Patch, $"/api/posts/{Id(draft)}", csrf: true, body: new { body_md = "x" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.SendAsync(HttpMethod.Patch, $"/api/posts/{Id(draft)}", csrf: true,
            body: new { base_revision = Revision(read), title = "  " })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Owner.SendAsync(HttpMethod.Patch, $"/api/posts/{Id(draft)}", csrf: false,
            body: new { base_revision = Revision(read), body_md = "x" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.SendAsync(HttpMethod.Patch, $"/api/posts/{Guid.NewGuid()}", csrf: true,
            body: new { base_revision = Revision(read), body_md = "x" })).Status);
    }

    [Fact]
    public async Task Approving_an_edit_with_edits_keeps_the_agents_revision_and_puts_the_owners_on_top()
    {
        await using var w = await PostTests.World.StartAsync();
        var draft = await w.DraftAsync("Launch", "v1");
        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/posts/{Id(draft)}/publish")).Status);
        var live = await w.OkAsync("read_post", new { post = Id(draft) });
        var ask = await w.OkAsync("edit_post", new { post = Id(draft), base_revision = Revision(live), body_md = "v2 by the agent, with one typo: teh." });

        var approve = await w.Owner.PostJsonAsync($"/api/requests/{ask["request"]!["id"]}/approve",
            new { edits = new { body_md = "v2 by the agent, with one typo fixed: the." } }, csrf: true);
        Assert.Equal(HttpStatusCode.OK, approve.Status);

        var revisions = await RevisionsAsync(w, Id(draft));
        Assert.Equal("Lenka", revisions[0]!["by_label"]!.GetValue<string>()); // the owner's fix, on top
        Assert.Equal("Claude", revisions[1]!["by_label"]!.GetValue<string>()); // the agent's words, kept as theirs
        var agentsRevision = await w.Owner.GetJsonAsync($"/api/posts/{Id(draft)}/revisions/{revisions[1]!["id"]}");
        Assert.Equal("v2 by the agent, with one typo: teh.", agentsRevision.Json!["body_md"]!.GetValue<string>());
        Assert.Equal("v2 by the agent, with one typo fixed: the.", (await w.OkAsync("read_post", new { post = Id(draft) }))["body_md"]!.GetValue<string>());
        Assert.Equal("approved", (await w.OkAsync("get_request", new { request = ask["request"]!["id"]!.GetValue<string>() }))["request"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Approving_a_publish_with_edits_publishes_the_owners_fix()
    {
        await using var w = await PostTests.World.StartAsync();
        var draft = await w.DraftAsync("Almost", "Nearly right.");
        var ask = await w.OkAsync("publish_post", new { post = Id(draft) });
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostJsonAsync($"/api/requests/{ask["request"]!["id"]}/approve",
            new { edits = new { title = "Right", body_md = "Exactly right." } }, csrf: true)).Status);

        var post = await w.OkAsync("read_post", new { post = Id(draft) });
        Assert.Equal("live", post["status"]!.GetValue<string>());
        Assert.Equal("Right", post["title"]!.GetValue<string>());
        Assert.Contains("Exactly right.", await (await w.Host.Browser().Http.GetAsync($"/posts/{post["slug"]}")).Content.ReadAsStringAsync());
        Assert.Equal("Lenka", (await RevisionsAsync(w, Id(draft)))[0]!["by_label"]!.GetValue<string>());
    }

    [Fact]
    public async Task Edits_are_refused_where_there_is_no_text_to_change_and_a_bad_edit_writes_nothing()
    {
        await using var w = await PostTests.World.StartAsync();
        var draft = await w.DraftAsync("Keep", "Keep me.");
        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/posts/{Id(draft)}/publish")).Status);
        var trash = await w.OkAsync("trash_post", new { post = Id(draft) });
        var trashId = trash["request"]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostJsonAsync($"/api/requests/{trashId}/approve",
            new { edits = new { body_md = "x" } }, csrf: true)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostJsonAsync($"/api/requests/{trashId}/decline",
            new { edits = new { body_md = "x" } }, csrf: true)).Status);

        var live = await w.OkAsync("read_post", new { post = Id(draft) });
        var edit = await w.OkAsync("edit_post", new { post = Id(draft), base_revision = Revision(live), body_md = "Agent's words." });
        var before = (await RevisionsAsync(w, Id(draft))).Count;
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostJsonAsync($"/api/requests/{edit["request"]!["id"]}/approve",
            new { edits = new { title = " " } }, csrf: true)).Status);
        Assert.Equal(before, (await RevisionsAsync(w, Id(draft))).Count); // nothing half-applied
        Assert.Equal("waiting", (await w.OkAsync("get_request", new { request = edit["request"]!["id"]!.GetValue<string>() }))["request"]!["state"]!.GetValue<string>());
        Assert.Equal("live", (await w.OkAsync("read_post", new { post = Id(draft) }))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_title_stored_before_the_one_line_rule_can_be_sent_back_unchanged_with_approve_with_edits()
    {
        await using var w = await PostTests.World.StartAsync();
        var draft = await w.DraftAsync("Old title", "Body.");
        // A revision from before the rule (or restored from an export) whose title carries a tab.
        var revision = Guid.CreateVersion7();
        await using (var conn = new NpgsqlConnection(w.Host.App.OwnerConnectionString))
        {
            await conn.ExecuteAsync("""
                INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, excerpt, actor, created_at, term_ids)
                SELECT @revision, blog_id, post_id, number + 1, 'Old' || chr(9) || 'title', body_md, excerpt, actor, now(), term_ids
                FROM post_revision WHERE id = (SELECT current_revision_id FROM post WHERE id = @id::uuid);
                UPDATE post SET current_revision_id = @revision WHERE id = @id::uuid;
                """, new { revision, id = Id(draft) });
        }

        // A publish: the app sends the whole form back, the stored title as it is, with the owner's fix to the body.
        var publish = await w.OkAsync("publish_post", new { post = Id(draft) });
        var approved = await w.Owner.PostJsonAsync($"/api/requests/{publish["request"]!["id"]}/approve",
            new { edits = new { title = "Old\ttitle", body_md = "Body, fixed." } }, csrf: true);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Json?.ToJsonString());
        var post = await w.OkAsync("read_post", new { post = Id(draft) });
        Assert.Equal("live", post["status"]!.GetValue<string>());
        Assert.Equal("Old\ttitle", post["title"]!.GetValue<string>());
        Assert.Equal("Body, fixed.", post["body_md"]!.GetValue<string>());

        // An edit, whose title carries over from the post: the same title back is not a new title either.
        var edit = await w.OkAsync("edit_post", new { post = Id(draft), base_revision = Revision(post), body_md = "The agent's words." });
        var editApproved = await w.Owner.PostJsonAsync($"/api/requests/{edit["request"]!["id"]}/approve",
            new { edits = new { title = "Old\ttitle", excerpt = "Short." } }, csrf: true);
        Assert.True(editApproved.Status == HttpStatusCode.OK, editApproved.Json?.ToJsonString());
        post = await w.OkAsync("read_post", new { post = Id(draft) });
        Assert.Equal("Old\ttitle", post["title"]!.GetValue<string>());
        Assert.Equal("The agent's words.", post["body_md"]!.GetValue<string>());
        Assert.Equal("Short.", post["excerpt"]!.GetValue<string>());

        // A title the owner changes is held to the rule, and refusing it writes nothing: not even the agent's revision.
        var again = await w.OkAsync("edit_post", new { post = Id(draft), base_revision = Revision(post), body_md = "Again." });
        var before = (await RevisionsAsync(w, Id(draft))).Count;
        var refused = await w.Owner.PostJsonAsync($"/api/requests/{again["request"]!["id"]}/approve",
            new { edits = new { title = "New\ttitle" } }, csrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("title is one line", refused.Json!.ToJsonString());
        Assert.Equal(before, (await RevisionsAsync(w, Id(draft))).Count);
        Assert.Equal("waiting", (await w.OkAsync("get_request", new { request = again["request"]!["id"]!.GetValue<string>() }))["request"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_editors_preview_is_what_readers_get_and_runs_nothing()
    {
        await using var w = await PostTests.World.StartAsync();
        var r = await w.Owner.PostJsonAsync("/api/preview", new { body_md = "# Hi\n\n<script>alert(1)</script>\n\n[x](javascript:alert(1))" }, csrf: true);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var html = r.Json!["html"]!.GetValue<string>();
        Assert.Contains("<h1", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.Host.Browser().PostJsonAsync("/api/preview", new { body_md = "x" }, csrf: true)).Status);
    }
}
