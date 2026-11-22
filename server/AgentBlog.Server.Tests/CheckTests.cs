using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// M2 slice 5 (docs/specs/check.md): <c>check_post</c>, the pre-flight an agent runs before asking. Deterministic rules;
/// outside links fetched only when asked and remembered a day; the same check on the owner's Inbox card.
/// </summary>
public sealed class CheckTests
{
    private static string Id(JsonNode n) => n["id"]!.GetValue<string>();

    private static async Task<JsonObject> CheckAsync(MediaTests.MediaWorld m, string post, object? more = null)
    {
        var args = new JsonObject { ["post"] = post };
        if (more is not null)
            foreach (var (k, v) in System.Text.Json.JsonSerializer.SerializeToNode(more)!.AsObject()) args[k] = v?.DeepClone();
        return (await m.OkAsync("check_post", args))["check"]!.AsObject();
    }

    private static string[] Checks(JsonObject check, string level) =>
        [.. check["findings"]!.AsArray().Where(f => f!["level"]!.GetValue<string>() == level).Select(f => f!["check"]!.GetValue<string>())];

    [Fact]
    public async Task A_clean_post_is_ready()
    {
        await using var m = await MediaTests.MediaWorld.StartAsync();
        var picture = await m.UploadAsync(MediaHost.Png);
        var post = (await m.OkAsync("create_draft", new
        {
            title = "How the trash became a feature",
            excerpt = "Nothing an agent does is lost: why every change is a revision.",
            // Code with HTML in it, and a comparison, are not HTML in the post.
            body_md = $"Intro words: if a < b and c > d, fine.\n\n## Why\n\nWrite `<div>` in code.\n\n```html\n<p>shown as code</p>\n```\n\n![The trash can, open]({picture["path"]})\n\n### Details\n\nMore.",
            categories = new[] { "Notes" },
        }))["post"]!;
        var check = await CheckAsync(m, Id(post));
        Assert.True(check["ready"]!.GetValue<bool>(), check.ToJsonString());
        Assert.Equal(0, check["warnings"]!.GetValue<int>());
        Assert.Equal("Nothing to fix.", check["summary"]!.GetValue<string>());
        Assert.Equal(1, check["stats"]!["pictures"]!.GetValue<int>());
        Assert.Equal(2, check["stats"]!["headings"]!.GetValue<int>());
    }

    [Fact]
    public async Task What_readers_would_hit_is_a_problem()
    {
        await using var m = await MediaTests.MediaWorld.StartAsync();
        var trashed = await m.UploadAsync(MediaHost.Gif, "image/gif", "gone.gif");
        var live = await m.UploadAsync(MediaHost.Png);
        await m.World.OwnerPostAsync($"/api/media/{Id(trashed)}/trash");
        var draft = (await m.OkAsync("create_draft", new { title = "Unpublished", body_md = "x" }))["post"]!;
        m.Web.Serve("https://example.com/missing", [], "text/html", HttpStatusCode.NotFound);
        m.Web.Serve("https://example.com/ok", [], "text/html");

        var post = (await m.OkAsync("create_draft", new
        {
            title = "Broken things",
            body_md = $"""
                ![](/media/00000000-0000-0000-0000-000000000001/nope.png)
                ![A gone cat]({trashed["path"]})
                ![Right id, wrong name](/media/{Id(live)}/another-name.png)
                See [the missing post](/posts/no-such-post) and [the draft](/posts/{draft["slug"]}).
                [Broken](https://example.com/missing) and [fine](https://example.com/ok).
                [Run me](javascript:alert(1))
                TODO: finish this.
                """,
        }))["post"]!;
        var check = await CheckAsync(m, Id(post));
        Assert.False(check["ready"]!.GetValue<bool>());
        var problems = Checks(check, "problem");
        Assert.Contains("alt_missing", problems);
        Assert.Equal(3, problems.Count(p => p == "media_missing")); // not in this blog's media; in the trash; the right id under another name
        Assert.Contains("link_internal", problems);                 // no such post
        Assert.Contains("link_internal", Checks(check, "warning")); // the draft: not live yet
        Assert.Contains("link_broken", problems);
        Assert.Contains("link_unsafe", problems);
        Assert.Contains("placeholder", problems);
        Assert.Equal(2, check["links"]!["outside"]!.GetValue<int>());
        Assert.Equal(1, check["links"]!["broken"]!.GetValue<int>());
        Assert.Contains("problem", check["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task What_is_worth_a_look_is_a_warning()
    {
        await using var m = await MediaTests.MediaWorld.StartAsync();
        m.Web.Serve("http://old.example/page", [], "text/html");
        m.Web.Serve("https://pics.example/cat.png", MediaHost.Png, "image/png");
        var post = (await m.OkAsync("create_draft", new
        {
            title = new string('T', 90),
            body_md = """
                # A second title

                #### Skipped two levels

                ![image](/somewhere.png) and ![cat.png](https://pics.example/cat.png)

                Read it [here](http://old.example/page). <b>bold</b>
                """,
        }))["post"]!;
        var warnings = Checks(await CheckAsync(m, Id(post)), "warning");
        foreach (var expected in new[] { "title_length", "excerpt_missing", "heading_h1", "heading_skip", "alt_weak", "link_text", "link_insecure", "raw_html", "image_external" })
            Assert.Contains(expected, warnings);
    }

    [Fact]
    public async Task A_post_that_repeats_another_is_named()
    {
        await using var m = await MediaTests.MediaWorld.StartAsync();
        var body = "Every change an agent makes is kept as a revision with its name on it, and the owner can put any of them back with one tap from the phone.";
        var first = (await m.OkAsync("create_draft", new { title = "Why nothing is ever lost", body_md = body }))["post"]!;
        var second = (await m.OkAsync("create_draft", new { title = "Why nothing is ever lost", body_md = body + " Also this." }))["post"]!;
        var unrelated = (await m.OkAsync("create_draft", new { title = "Baking bread at home", body_md = "Flour, water, salt and patience make a good loaf of bread in a home oven." }))["post"]!;

        var check = await CheckAsync(m, Id(second));
        var similar = check["similar_posts"]!.AsArray();
        Assert.Equal(Id(first), Id(similar[0]!));
        Assert.Contains("similar_post", Checks(check, "warning"));
        Assert.DoesNotContain(similar, s => Id(s!) == Id(unrelated));
        Assert.DoesNotContain(similar, s => Id(s!) == Id(second)); // never itself
        Assert.Empty((await CheckAsync(m, Id(unrelated)))["similar_posts"]!.AsArray());

        // Two short posts sharing a few words aren't repeats: too little text to say.
        // "Short notes for the week ahead/behind" share 3 of 5 three-word runs: 60% alike, and still not a repeat.
        var a = (await m.OkAsync("create_draft", new { title = "Monday", body_md = "Short notes for the week ahead." }))["post"]!;
        await m.OkAsync("create_draft", new { title = "Tuesday", body_md = "Short notes for the week behind." });
        Assert.Empty((await CheckAsync(m, Id(a)))["similar_posts"]!.AsArray());
    }

    [Fact]
    public async Task New_text_is_checked_before_it_is_saved()
    {
        await using var m = await MediaTests.MediaWorld.StartAsync();
        var post = (await m.OkAsync("create_draft", new { title = "Fine", excerpt = "A fine excerpt.", body_md = "Fine words." }))["post"]!;
        var dry = await m.OkAsync("check_post", new { post = Id(post), body_md = "# Oops\n\nTODO" });
        Assert.Equal("your new text", dry["checked"]!.GetValue<string>());
        Assert.Contains("placeholder", Checks(dry["check"]!.AsObject(), "problem"));
        Assert.Equal("Fine words.", (await m.OkAsync("read_post", new { post = Id(post) }))["body_md"]!.GetValue<string>()); // nothing saved
        Assert.Equal("saved", (await m.OkAsync("check_post", new { post = Id(post) }))["checked"]!.GetValue<string>());
        Assert.True((await m.ToolAsync("check_post", new { post = Guid.NewGuid().ToString() })).Refused);
    }

    [Fact]
    public async Task Outside_links_are_fetched_only_when_asked_remembered_a_day_and_capped()
    {
        await using var m = await MediaTests.MediaWorld.StartAsync();
        var links = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"[Page {i}](https://site{i}.example/)"));
        var post = (await m.OkAsync("create_draft", new { title = "Many links", body_md = links }))["post"]!;

        var quiet = await CheckAsync(m, Id(post), new { check_links = false });
        Assert.Empty(m.Web.Fetched);
        Assert.Equal(30, quiet["links"]!["not_checked"]!.GetValue<int>());

        var first = await CheckAsync(m, Id(post));
        Assert.Equal(25, m.Web.Fetched.Distinct().Count()); // at most 25 a check
        Assert.Equal(25, first["links"]!["checked"]!.GetValue<int>());
        Assert.Contains("links_not_checked", Checks(first, "note"));
        Assert.Equal(25, first["links"]!["broken"]!.GetValue<int>()); // FakeWeb answers 404 for anything it doesn't serve

        var second = await CheckAsync(m, Id(post));
        Assert.Equal(30, m.Web.Fetched.Distinct().Count()); // only the 5 left; the 25 are remembered
        Assert.Equal(30, second["links"]!["checked"]!.GetValue<int>());

        // A day later the answers are old: fetched again.
        await using (var conn = new Npgsql.NpgsqlConnection(m.World.Host.App.OwnerConnectionString))
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE link_check SET checked_at = now() - interval '25 hours'");
        var before = m.Web.Fetched.Count;
        await CheckAsync(m, Id(post));
        Assert.Equal(before + 25, m.Web.Fetched.Count);
    }

    [Fact]
    public async Task The_real_client_counts_a_link_inside_the_network_as_broken_without_connecting()
    {
        await using var m = await MediaTests.MediaWorld.StartAsync(realFetch: true);
        var post = (await m.OkAsync("create_draft", new { title = "Inside", body_md = "[admin](http://127.0.0.1/admin) [meta](http://169.254.169.254/latest/)" }))["post"]!;
        var check = await CheckAsync(m, Id(post));
        var broken = check["findings"]!.AsArray().Where(f => f!["check"]!.GetValue<string>() == "link_broken").ToList();
        Assert.Equal(2, broken.Count);
        Assert.All(broken, f => Assert.Contains("public internet", f!["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_owner_sees_the_check_of_exactly_what_is_asked_on_the_inbox_card_without_any_fetch()
    {
        await using var m = await MediaTests.MediaWorld.StartAsync();
        m.Web.Serve("https://example.com/missing", [], "text/html", HttpStatusCode.NotFound);
        var post = (await m.OkAsync("create_draft", new { title = "Ask", excerpt = "An excerpt.", body_md = "Clean words, a [link](https://example.com/missing)." }))["post"]!;
        await CheckAsync(m, Id(post)); // the agent checked: the link's answer is remembered
        var fetched = m.Web.Fetched.Count;
        await m.OkAsync("publish_post", new { post = Id(post) }); // at Propose: a request to the owner

        var card = (await m.World.Owner.GetJsonAsync("/api/requests")).Json!["requests"]!.AsArray().Single()!;
        Assert.Equal("publish", card["action"]!.GetValue<string>());
        Assert.Contains("link_broken", Checks(card["checks"]!.AsObject(), "problem"));
        Assert.Equal(fetched, m.Web.Fetched.Count); // the Inbox fetched nothing
        Assert.Equal(HttpStatusCode.OK, (await m.World.Owner.PostJsonAsync($"/api/requests/{card["id"]}/decline", null, csrf: true)).Status);

        // An edit to a live post is checked as its new words, not the post's current ones.
        var other = (await m.OkAsync("create_draft", new { title = "Live one", excerpt = "An excerpt.", body_md = "Clean." }))["post"]!;
        Assert.Equal(HttpStatusCode.OK, (await m.World.OwnerPostAsync($"/api/posts/{Id(other)}/publish")).Status);
        var live = await m.OkAsync("read_post", new { post = Id(other) });
        await m.OkAsync("edit_post", new { post = Id(other), base_revision = live["revision"]!.GetValue<string>(), body_md = "Now with TODO left in." });
        var edit = (await m.World.Owner.GetJsonAsync("/api/requests")).Json!["requests"]!.AsArray().Single()!;
        Assert.Equal("edit", edit["action"]!.GetValue<string>());
        Assert.Contains("placeholder", Checks(edit["checks"]!.AsObject(), "problem"));

        // The owner can run it on any post, from the app.
        var own = await m.World.Owner.GetJsonAsync($"/api/posts/{Id(post)}/check?links=false");
        Assert.Equal(HttpStatusCode.OK, own.Status);
        Assert.NotNull(own.Json!["findings"]);
        Assert.Equal(HttpStatusCode.NotFound, (await m.World.Owner.GetJsonAsync($"/api/posts/{Guid.NewGuid()}/check")).Status);
    }
}
