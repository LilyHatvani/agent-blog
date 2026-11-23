using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// M3 (docs/specs/history.md): every agent action in History is reversible from there, by the owner, judged against the
/// post as it is now; an undo is an ordinary owner action (a new revision or a state change), recorded, and done once.
/// </summary>
public sealed class UndoTests
{
    private static async Task<string> BlogAsync(PostTests.World w) => (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();

    private static async Task<JsonArray> HistoryAsync(PostTests.World w, SignInBrowser? who = null) =>
        (await (who ?? w.Owner).GetJsonAsync($"/api/blogs/{await BlogAsync(w)}/history")).Json!["events"]!.AsArray();

    /// <summary>The newest event of this kind by the agent.</summary>
    private static async Task<JsonNode> AgentEventAsync(PostTests.World w, string kind) =>
        (await HistoryAsync(w)).First(e => e!["kind"]!.GetValue<string>() == kind && e["actor"]!.GetValue<string>().StartsWith("connection:"))!;

    private static async Task<JsonResult> UndoAsync(PostTests.World w, JsonNode e, SignInBrowser? who = null) =>
        await (who ?? w.Owner).PostJsonAsync($"/api/blogs/{await BlogAsync(w)}/history/{e["id"]}/undo", null, csrf: true);

    private static async Task<JsonNode> PostAsync(PostTests.World w, JsonNode post) => (await w.Owner.GetJsonAsync($"/api/posts/{post["id"]}")).Json!;

    [Fact]
    public async Task Undoing_an_agents_edit_puts_the_earlier_words_back_as_a_new_revision_once()
    {
        await using var w = await PostTests.World.StartAsync();
        var post = await w.DraftAsync("Hello", "My words.");
        await w.OkAsync("edit_post", new { post = post["id"]!.ToString(), base_revision = post["revision"]!.GetValue<string>(), body_md = "The agent's words." });

        var edit = await AgentEventAsync(w, "revision_saved");
        Assert.Equal("Put revision 1 back", edit["undo"]!["label"]!.GetValue<string>());
        Assert.Null(edit["undo"]!["why_not"]);
        Assert.Equal(HttpStatusCode.NoContent, (await UndoAsync(w, edit)).Status);

        var now = await PostAsync(w, post);
        Assert.Equal("My words.", now["body_md"]!.GetValue<string>());
        Assert.Equal(3, now["post"]!["revision_number"]!.GetValue<int>()); // nothing lost: the agent's revision stays in history
        var again = await AgentEventAsync(w, "revision_saved");
        Assert.True(again["undo"]!["done"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Conflict, (await UndoAsync(w, again)).Status);
        Assert.Contains(await HistoryAsync(w), e => e!["kind"]!.GetValue<string>() == "action_undone");
    }

    [Fact]
    public async Task An_undo_that_no_longer_applies_says_why_and_changes_nothing()
    {
        await using var w = await PostTests.World.StartAsync();
        var post = await w.DraftAsync("Hello", "My words.");
        var edited = (await w.OkAsync("edit_post", new { post = post["id"]!.ToString(), base_revision = post["revision"]!.GetValue<string>(), body_md = "Agent." }))["post"]!;
        // The owner changed it since: undoing the agent's edit would throw the owner's away.
        await w.Owner.SendAsync(HttpMethod.Patch, $"/api/posts/{post["id"]}", csrf: true,
            body: new { base_revision = edited["revision"]!.GetValue<string>(), body_md = "Owner." });

        var edit = await AgentEventAsync(w, "revision_saved");
        Assert.Contains("changed since", edit["undo"]!["why_not"]!.GetValue<string>());
        var refused = await UndoAsync(w, edit);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("Owner.", (await PostAsync(w, post))["body_md"]!.GetValue<string>());
    }

    [Fact]
    public async Task Each_post_action_an_agent_took_has_its_inverse()
    {
        await using var w = await PostTests.World.StartAsync();
        await w.SetLevelAsync("full");
        var post = await w.DraftAsync("Story", "Words.");

        await w.OkAsync("publish_post", new { post = post["id"]!.ToString() });
        Assert.Equal(HttpStatusCode.NoContent, (await UndoAsync(w, await AgentEventAsync(w, "post_published"))).Status);
        Assert.Equal("draft", (await PostAsync(w, post))["post"]!["status"]!.GetValue<string>());

        await w.OkAsync("publish_post", new { post = post["id"]!.ToString() });
        await w.OkAsync("unpublish_post", new { post = post["id"]!.ToString() });
        Assert.Equal(HttpStatusCode.NoContent, (await UndoAsync(w, await AgentEventAsync(w, "post_unpublished"))).Status);
        Assert.Equal("live", (await PostAsync(w, post))["post"]!["status"]!.GetValue<string>());

        await w.OkAsync("trash_post", new { post = post["id"]!.ToString() });
        Assert.Equal(HttpStatusCode.NoContent, (await UndoAsync(w, await AgentEventAsync(w, "post_trashed"))).Status);
        Assert.Equal("live", (await PostAsync(w, post))["post"]!["status"]!.GetValue<string>());

        var at = DateTimeOffset.UtcNow.AddDays(1).ToString("O");
        var other = await w.DraftAsync("Later", "Words.");
        await w.OkAsync("schedule_post", new { post = other["id"]!.ToString(), at });
        Assert.Equal(HttpStatusCode.NoContent, (await UndoAsync(w, await AgentEventAsync(w, "post_scheduled"))).Status);
        Assert.Equal("draft", (await PostAsync(w, other))["post"]!["status"]!.GetValue<string>());

        // A new draft: undoing it moves it to the trash (from where it restores; nothing is deleted).
        Assert.Equal(HttpStatusCode.NoContent, (await UndoAsync(w, await AgentEventAsync(w, "post_created"))).Status);
        Assert.Equal("trash", (await PostAsync(w, other))["post"]!["status"]!.GetValue<string>());
        var restored = (await HistoryAsync(w)).First(e => e!["kind"]!.GetValue<string>() == "post_created" && e["post"]!.GetValue<string>() == post["id"]!.ToString())!;
        Assert.Null(restored["undo"]!["why_not"]);
    }

    [Fact]
    public async Task Undoing_a_picture_an_agent_added_moves_it_to_the_trash()
    {
        var web = new FakeWeb();
        web.Serve("https://pics.example/cat.png", MediaHost.Png, "image/png");
        await using var w = await PostTests.World.StartAsync(MediaHost.Configure(new FakeBucket(), web));
        var media = (await w.OkAsync("upload_media_from_url", new { url = "https://pics.example/cat.png", alt = "A cat" }))["media"]!;

        var added = await AgentEventAsync(w, "media_added");
        Assert.Equal("Move the picture to the trash", added["undo"]!["label"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await UndoAsync(w, added)).Status);
        var trash = (await w.Owner.GetJsonAsync($"/api/blogs/{await BlogAsync(w)}/media?status=trash")).Json!["media"]!.AsArray();
        Assert.Contains(trash, m => m!["id"]!.GetValue<string>() == media["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_the_owner_undoes_and_only_what_an_agent_did()
    {
        await using var c = await PeoplesAgentsTests.Crew.StartAsync();
        var post = await c.W.DraftAsync("Hello", "Words.");
        await c.W.OkAsync("edit_post", new { post = post["id"]!.ToString(), base_revision = post["revision"]!.GetValue<string>(), body_md = "Agent." });
        var edit = await AgentEventAsync(c.W, "revision_saved");

        // An editor reads History but has no undo, and can't call one.
        Assert.All(await HistoryAsync(c.W, c.Ben), e => Assert.Null(e!["undo"]));
        Assert.Equal(HttpStatusCode.NotFound, (await UndoAsync(c.W, edit, c.Ben)).Status);

        // People's own actions have no undo here: theirs to change by hand.
        var mine = await c.PersonDraftAsync(c.Owner, "Mine");
        var created = (await HistoryAsync(c.W)).First(e => e!["kind"]!.GetValue<string>() == "post_created" && e["post"]!.GetValue<string>() == mine["id"]!.ToString())!;
        Assert.Null(created["undo"]);
        Assert.Equal(HttpStatusCode.Conflict, (await UndoAsync(c.W, created)).Status);

        await using var conn = new NpgsqlConnection(c.Host.App.OwnerConnectionString);
        Assert.Equal(0, await conn.QuerySingleAsync<int>("SELECT count(*)::int FROM event WHERE kind = 'action_undone'"));
    }
}
