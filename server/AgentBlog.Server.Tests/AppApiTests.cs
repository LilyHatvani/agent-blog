using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// The owner API the phone app reads (docs/specs/app.md): previews in the readers' HTML, a revision's content, exactly
/// what a request asks, and the blog's history in words.
/// </summary>
public sealed class AppApiTests
{
    private static string Id(JsonNode post) => post["id"]!.GetValue<string>();

    [Fact]
    public async Task A_post_comes_with_its_preview_and_revisions_name_their_author_in_words()
    {
        await using var w = await PostTests.World.StartAsync();
        var post = (await w.OkAsync("create_draft", new { title = "Hi", body_md = "Some **bold** words. <script>x</script>" }))["post"]!;

        var detail = (await w.Owner.GetJsonAsync($"/api/posts/{Id(post)}")).Json!;
        Assert.Contains("<strong>bold</strong>", detail["html"]!.GetValue<string>());
        Assert.DoesNotContain("<script>", detail["html"]!.GetValue<string>());
        var revision = detail["revisions"]![0]!;
        Assert.Equal("Claude", revision["by_label"]!.GetValue<string>());

        var content = (await w.Owner.GetJsonAsync($"/api/posts/{Id(post)}/revisions/{revision["id"]!.GetValue<string>()}")).Json!;
        Assert.Equal(1, content["number"]!.GetValue<int>());
        Assert.Contains("<strong>bold</strong>", content["html"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.GetJsonAsync($"/api/posts/{Id(post)}/revisions/{Guid.NewGuid()}")).Status);
    }

    [Fact]
    public async Task A_request_shows_exactly_what_the_agent_asked_and_whether_it_still_applies()
    {
        await using var w = await PostTests.World.StartAsync();
        var post = (await w.OkAsync("create_draft", new { title = "Hi", body_md = "Old words." }))["post"]!;
        await w.OwnerPostAsync($"/api/posts/{Id(post)}/publish");
        await w.OkAsync("edit_post", new { post = Id(post), base_revision = post["revision"]!.GetValue<string>(), title = "Hi again", body_md = "New *words*." });

        var request = Assert.Single((await w.Owner.GetJsonAsync("/api/requests")).Json!["requests"]!.AsArray())!;
        Assert.Equal("edit", request["action"]!.GetValue<string>());
        Assert.Equal("Hi again", request["proposal"]!["title"]!.GetValue<string>());
        Assert.Contains("<em>words</em>", request["proposal"]!["html"]!.GetValue<string>());
        Assert.True(request["still_applies"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_history_tells_who_did_what_in_words_newest_first()
    {
        await using var w = await PostTests.World.StartAsync();
        var post = (await w.OkAsync("create_draft", new { title = "Story", body_md = "x" }))["post"]!;
        await w.OkAsync("publish_post", new { post = Id(post) }); // waits for the owner

        var blogId = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
        var events = (await w.Owner.GetJsonAsync($"/api/blogs/{blogId}/history")).Json!["events"]!.AsArray();
        var newest = events[0]!;
        Assert.Equal("approval_requested", newest["kind"]!.GetValue<string>());
        Assert.Equal("Claude", newest["actor_label"]!.GetValue<string>());
        Assert.Equal("Story", newest["subject"]!.GetValue<string>());
        Assert.Equal(Id(post), newest["post"]!.GetValue<string>());
        Assert.Contains(events, e => e!["kind"]!.GetValue<string>() == "connection_created" && e["actor_label"]!.GetValue<string>() == "Lenka");

        var older = (await w.Owner.GetJsonAsync($"/api/blogs/{blogId}/history?before={newest["id"]}")).Json!["events"]!.AsArray();
        Assert.DoesNotContain(older, e => e!["id"]!.GetValue<long>() >= newest["id"]!.GetValue<long>());
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.GetJsonAsync($"/api/blogs/{Guid.NewGuid()}/history")).Status);
    }
}
