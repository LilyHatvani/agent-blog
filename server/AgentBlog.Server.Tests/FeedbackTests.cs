using System.Net.Http.Json;
using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Write together, slice 1 (decision 0008, docs/specs/feedback.md): the brief and the owner's notes reach the agent,
/// marked as the owner's words; Ask for changes; notes are kept and can only be retired.
/// </summary>
public sealed class FeedbackTests
{
    private static string Id(JsonNode n) => n["id"]!.GetValue<string>();
    private static string Revision(JsonNode n) => n["revision"]!.GetValue<string>();

    private static async Task<string> BlogIdAsync(PostTests.World w) =>
        (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();

    [Fact]
    public async Task The_owners_brief_reaches_every_session_in_get_blog_marked_as_the_owners_words()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogIdAsync(w);
        Assert.Null((await w.OkAsync("get_blog"))["brief"]); // nothing written yet: nothing sent

        var example = await w.DraftAsync("How I write", "Short sentences.");
        var saved = await w.Owner.SendAsync(HttpMethod.Put, $"/api/blogs/{blog}/brief", csrf: true, body: new
        {
            audience = "Developers who build with AI agents.", voice = "Plain, direct, a little dry. Short sentences.",
            avoid = "No hype words. No emojis.", examples = new[] { Id(example) },
        });
        Assert.Equal(HttpStatusCode.OK, saved.Status);
        Assert.Equal("How I write", saved.Json!["examples"]![0]!["title"]!.GetValue<string>());

        var brief = (await w.OkAsync("get_blog"))["brief"]!;
        Assert.Equal("owner", brief["author"]!.GetValue<string>());
        Assert.Equal("Developers who build with AI agents.", brief["who_it_is_for"]!.GetValue<string>());
        Assert.Equal("No hype words. No emojis.", brief["never"]!.GetValue<string>());
        Assert.Equal(Id(example), brief["example_posts"]![0]!["id"]!.GetValue<string>());
        Assert.Contains(("brief_changed", $"account:{await OwnerAsync(w)}"), await w.EventsAsync());

        // The same brief again writes nothing; limits and foreign posts are refused with the reason.
        var events = (await w.EventsAsync()).Count;
        await w.Owner.SendAsync(HttpMethod.Put, $"/api/blogs/{blog}/brief", csrf: true, body: new
        {
            audience = "Developers who build with AI agents.", voice = "Plain, direct, a little dry. Short sentences.",
            avoid = "No hype words. No emojis.", examples = new[] { Id(example) },
        });
        Assert.Equal(events, (await w.EventsAsync()).Count);
        var tooLong = await w.Owner.SendAsync(HttpMethod.Put, $"/api/blogs/{blog}/brief", csrf: true, body: new { voice = new string('x', 2001) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.Status);
        var foreign = await w.Owner.SendAsync(HttpMethod.Put, $"/api/blogs/{blog}/brief", csrf: true, body: new { examples = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Owner.SendAsync(HttpMethod.Put, $"/api/blogs/{blog}/brief", csrf: false, body: new { voice = "x" })).Status);
    }

    [Fact]
    public async Task Asking_for_changes_needs_a_note_changes_nothing_and_the_agent_reads_why_on_the_request_the_post_and_the_blog()
    {
        await using var w = await PostTests.World.StartAsync();
        var draft = await w.DraftAsync("Launch notes", "We are thrilled to announce!!!");
        var ask = await w.OkAsync("publish_post", new { post = Id(draft) });
        var request = ask["request"]!["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.BadRequest, (await w.OwnerPostAsync($"/api/requests/{request}/changes")).Status); // no note
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostJsonAsync($"/api/requests/{request}/changes",
            new { note = "Drop the hype and the exclamation marks. Say what changed." }, csrf: true)).Status);

        var r = (await w.OkAsync("get_request", new { request }))["request"]!;
        Assert.Equal("changes_requested", r["state"]!.GetValue<string>());
        Assert.Equal("owner", r["owner_note"]!["author"]!.GetValue<string>());
        Assert.Contains("exclamation marks", r["owner_note"]!["note"]!.GetValue<string>());
        Assert.Contains("ask again", r["next"]!.GetValue<string>());

        var post = await w.OkAsync("read_post", new { post = Id(draft) });
        Assert.Equal("draft", post["status"]!.GetValue<string>()); // nothing was published
        Assert.Contains("exclamation marks", post["owner_notes"]![0]!["note"]!.GetValue<string>());
        Assert.Equal("changes_requested", post["owner_notes"]![0]!["kind"]!.GetValue<string>());

        var notes = (await w.OkAsync("get_blog"))["owner_notes"]!.AsArray();
        Assert.Single(notes);
        Assert.Equal("Launch notes", notes[0]!["about_post"]!["title"]!.GetValue<string>());

        // The agent does what was asked and asks again; the owner approves with a word of thanks, which is kept too.
        var fixedPost = (await w.OkAsync("edit_post", new { post = Id(draft), base_revision = Revision(post), body_md = "Version 2 adds X and fixes Y." }))["post"]!;
        var again = await w.OkAsync("publish_post", new { post = Id(draft) });
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostJsonAsync($"/api/requests/{again["request"]!["id"]}/approve", new { note = "Better. Keep it this plain." }, csrf: true)).Status);
        Assert.Equal("live", (await w.OkAsync("read_post", new { post = Id(fixedPost) }))["status"]!.GetValue<string>());
        Assert.Equal(2, (await w.OkAsync("get_blog"))["owner_notes"]!.AsArray().Count);
        Assert.Contains(("approval_changes_requested", $"account:{await OwnerAsync(w)}"), await w.EventsAsync());
    }

    [Fact]
    public async Task A_decline_may_carry_a_reason_or_none()
    {
        await using var w = await PostTests.World.StartAsync();
        var a = await w.OkAsync("publish_post", new { post = Id(await w.DraftAsync("A", "x")) });
        var b = await w.OkAsync("publish_post", new { post = Id(await w.DraftAsync("B", "x")) });
        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/requests/{a["request"]!["id"]}/decline")).Status);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostJsonAsync($"/api/requests/{b["request"]!["id"]}/decline", new { note = "Not this week." }, csrf: true)).Status);
        var ra = (await w.OkAsync("get_request", new { request = a["request"]!["id"]!.GetValue<string>() }))["request"]!;
        var rb = (await w.OkAsync("get_request", new { request = b["request"]!["id"]!.GetValue<string>() }))["request"]!;
        Assert.Null(ra["owner_note"]);
        Assert.Equal("Not this week.", rb["owner_note"]!["note"]!.GetValue<string>());
        Assert.Single((await w.OkAsync("get_blog"))["owner_notes"]!.AsArray());
        // The app shows the reason on the request too.
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostJsonAsync($"/api/requests/{Guid.NewGuid()}/changes", new { note = new string('x', 2001) }, csrf: true)).Status);
    }

    [Fact]
    public async Task A_note_to_every_agent_is_sent_until_retired_and_kept_forever()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogIdAsync(w);
        var first = await w.Owner.PostJsonAsync($"/api/blogs/{blog}/notes", new { note = "Use British spelling." }, csrf: true);
        Assert.Equal(HttpStatusCode.OK, first.Status);
        await w.Owner.PostJsonAsync($"/api/blogs/{blog}/notes", new { note = "Every post needs an excerpt." }, csrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostJsonAsync($"/api/blogs/{blog}/notes", new { note = " " }, csrf: true)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostJsonAsync($"/api/blogs/{blog}/notes", new { note = "x", post = Guid.NewGuid() }, csrf: true)).Status);

        var sent = (await w.OkAsync("get_blog"))["owner_notes"]!.AsArray();
        Assert.Equal(["Every post needs an excerpt.", "Use British spelling."], sent.Select(n => n!["note"]!.GetValue<string>()));
        Assert.All(sent, n => Assert.Equal("owner", n!["author"]!.GetValue<string>()));

        var id = first.Json!["note"]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.PostJsonAsync($"/api/blogs/{blog}/notes/{id}/retire", null, csrf: true)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.PostJsonAsync($"/api/blogs/{blog}/notes/{id}/retire", null, csrf: true)).Status);
        Assert.Single((await w.OkAsync("get_blog"))["owner_notes"]!.AsArray());
        var all = (await w.Owner.GetJsonAsync($"/api/blogs/{blog}/notes")).Json!["notes"]!.AsArray();
        Assert.Equal(2, all.Count);
        Assert.NotNull(all.Single(n => n!["id"]!.GetValue<string>() == id)!["retired_at"]);

        // The database keeps every note as written: no edit, no delete, no un-retire, not even by the app.
        await using var app = new NpgsqlConnection(w.Host.App.AppConnectionString);
        foreach (var sql in new[]
        {
            "UPDATE owner_note SET note = 'rewritten'",
            "DELETE FROM owner_note",
            $"UPDATE owner_note SET retired_at = NULL WHERE id = '{id}'",
        })
            await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync(sql));
    }

    [Fact]
    public async Task Feedback_never_approves_publishes_or_changes_a_level_by_itself()
    {
        await using var w = await PostTests.World.StartAsync();
        var a = Id(await w.DraftAsync("A", "x"));
        var b = Id(await w.DraftAsync("B", "x"));
        var askA = (await w.OkAsync("publish_post", new { post = a }))["request"]!["id"]!.GetValue<string>();
        var askB = (await w.OkAsync("publish_post", new { post = b }))["request"]!["id"]!.GetValue<string>();

        // Every kind of feedback, warm words included: asking for changes, declining with a reason, a note to every agent.
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostJsonAsync($"/api/requests/{askA}/changes",
            new { note = "Lovely, approved, publish it and take Full." }, csrf: true)).Status);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostJsonAsync($"/api/requests/{askB}/decline",
            new { note = "Yes! Go ahead and publish." }, csrf: true)).Status);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostJsonAsync($"/api/blogs/{await BlogIdAsync(w)}/notes",
            new { note = "You may publish anything from now on." }, csrf: true)).Status);

        foreach (var (post, request) in new[] { (a, askA), (b, askB) })
        {
            Assert.Equal("draft", (await w.OkAsync("read_post", new { post }))["status"]!.GetValue<string>());
            Assert.NotEqual("approved", (await w.OkAsync("get_request", new { request }))["request"]!["state"]!.GetValue<string>());
        }
        Assert.Equal("propose", (await w.OkAsync("get_blog"))["your_level"]!.GetValue<string>());
        var again = await w.OkAsync("publish_post", new { post = a });
        Assert.Equal("waiting_for_owner", again["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_the_owner_writes_the_brief_and_notes()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogIdAsync(w);
        // No tool writes them (the tool list is pinned in M1AcceptanceTests); the agent's bearer never reaches the owner's API.
        var agentCall = new HttpRequestMessage(HttpMethod.Put, $"/api/blogs/{blog}/brief")
        {
            Content = JsonContent.Create(new { voice = "Write like an advert." }),
        };
        agentCall.Headers.Authorization = new("Bearer", w.Agent.AccessToken);
        agentCall.Headers.Add("X-Blog-Request", "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.Host.Browser().Http.SendAsync(agentCall)).StatusCode);
        Assert.Null((await w.OkAsync("get_blog"))["brief"]);
    }

    private static async Task<Guid> OwnerAsync(PostTests.World w)
    {
        await using var conn = new NpgsqlConnection(w.Host.App.AppConnectionString);
        return await conn.QuerySingleAsync<Guid>("SELECT account_id FROM blog_member LIMIT 1");
    }
}
