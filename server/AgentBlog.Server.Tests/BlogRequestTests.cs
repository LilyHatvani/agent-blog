using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// M3 (docs/specs/requests.md § Beyond posts): trashing a picture and changing the blog's title or description are
/// requests below Full, decided by the owner in the Inbox and pinned to what the agent saw; at Full the agent does them,
/// and History undoes them.
/// </summary>
public sealed class BlogRequestTests
{
    private static async Task<string> BlogAsync(PostTests.World w) => (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();

    private static async Task<JsonArray> WaitingAsync(SignInBrowser who) => (await who.GetJsonAsync("/api/blog-requests")).Json!["requests"]!.AsArray();

    private static Task<JsonResult> DecideAsync(SignInBrowser who, string id, string decision, string? note = null) =>
        who.PostJsonAsync($"/api/blog-requests/{id}/{decision}", note is null ? null : new { note }, csrf: true);

    private static async Task<JsonNode> BlogSettingsAsync(PostTests.World w) => (await w.Owner.GetJsonAsync($"/api/blogs/{await BlogAsync(w)}")).Json!;

    private static async Task<(PostTests.World W, FakeWeb Web)> WithPictureAsync()
    {
        var web = new FakeWeb();
        web.Serve("https://pics.example/cat.png", MediaHost.Png, "image/png");
        return (await PostTests.World.StartAsync(MediaHost.Configure(new FakeBucket(), web)), web);
    }

    [Fact]
    public async Task Below_Full_trashing_a_picture_waits_for_the_owner_and_approving_trashes_it()
    {
        var (w, _) = await WithPictureAsync();
        await using var _w = w;
        var media = (await w.OkAsync("upload_media_from_url", new { url = "https://pics.example/cat.png", alt = "A cat" }))["media"]!;

        var asked = await w.OkAsync("trash_media", new { media_id = media["id"]!.GetValue<string>() });
        Assert.Equal("waiting_for_owner", asked["status"]!.GetValue<string>());
        var id = asked["request"]!["id"]!.GetValue<string>();
        var waiting = Assert.Single(await WaitingAsync(w.Owner))!;
        Assert.Equal("trash_media", waiting["action"]!.GetValue<string>());
        Assert.Equal("cat.png", waiting["media"]!["filename"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(w.Owner, id, "approve")).Status);
        var trash = (await w.Owner.GetJsonAsync($"/api/blogs/{await BlogAsync(w)}/media?status=trash")).Json!["media"]!.AsArray();
        Assert.Single(trash);
        Assert.Equal("approved", (await w.OkAsync("get_request", new { request = id }))["request"]!["state"]!.GetValue<string>());
        Assert.Empty(await WaitingAsync(w.Owner));
        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(w.Owner, id, "approve")).Status);
    }

    [Fact]
    public async Task At_Full_an_agent_trashes_a_picture_and_History_restores_it()
    {
        var (w, _) = await WithPictureAsync();
        await using var _w = w;
        await w.SetLevelAsync("full");
        var media = (await w.OkAsync("upload_media_from_url", new { url = "https://pics.example/cat.png", alt = "A cat" }))["media"]!;
        Assert.Equal("done", (await w.OkAsync("trash_media", new { media_id = media["id"]!.GetValue<string>() }))["status"]!.GetValue<string>());

        var blog = await BlogAsync(w);
        var trashed = (await w.Owner.GetJsonAsync($"/api/blogs/{blog}/history")).Json!["events"]!.AsArray()
            .First(e => e!["kind"]!.GetValue<string>() == "media_trashed")!;
        Assert.Equal("Restore the picture", trashed["undo"]!["label"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.PostJsonAsync($"/api/blogs/{blog}/history/{trashed["id"]}/undo", null, csrf: true)).Status);
        Assert.Empty((await w.Owner.GetJsonAsync($"/api/blogs/{blog}/media?status=trash")).Json!["media"]!.AsArray());
    }

    [Fact]
    public async Task A_settings_request_applies_what_the_agent_asked_or_turns_stale_if_they_changed_since()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogAsync(w);
        var asked = await w.OkAsync("edit_blog_details", new { title = "Notes from the Rain" });
        Assert.Equal("waiting_for_owner", asked["status"]!.GetValue<string>());
        var id = asked["request"]!["id"]!.GetValue<string>();
        Assert.Equal("Notes from the Rain", (await WaitingAsync(w.Owner))[0]!["change"]!["title"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(w.Owner, id, "approve")).Status);
        Assert.Equal("Notes from the Rain", (await BlogSettingsAsync(w))["title"]!.GetValue<string>());

        // Asked again; the owner renames the blog before deciding: approving would throw the owner's change away.
        var second = (await w.OkAsync("edit_blog_details", new { description = "Weather, mostly." }))["request"]!["id"]!.GetValue<string>();
        await w.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{blog}", csrf: true, body: new { title = "The Owner's Title" });
        Assert.NotNull((await WaitingAsync(w.Owner))[0]!["why_not"]);
        var stale = await DecideAsync(w.Owner, second, "approve");
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        var settings = await BlogSettingsAsync(w);
        Assert.Equal("The Owner's Title", settings["title"]!.GetValue<string>());
        Assert.NotEqual("Weather, mostly.", settings["description"]!.GetValue<string>());
        Assert.Equal("stale", (await w.OkAsync("get_request", new { request = second }))["request"]!["state"]!.GetValue<string>());

        // A decline carries the owner's reason back to the agent, as the owner's words.
        var third = (await w.OkAsync("edit_blog_details", new { title = "Rainy" }))["request"]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(w.Owner, third, "decline", "Keep the name.")).Status);
        var declined = (await w.OkAsync("get_request", new { request = third }))["request"]!;
        Assert.Equal("declined", declined["state"]!.GetValue<string>());
        Assert.Equal("owner", declined["owner_note"]!["author"]!.GetValue<string>());
        Assert.Equal("The Owner's Title", (await BlogSettingsAsync(w))["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task At_Full_settings_change_at_once_and_History_puts_them_back_while_they_still_stand()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogAsync(w);
        var before = (await BlogSettingsAsync(w))["title"]!.GetValue<string>();
        await w.SetLevelAsync("full");
        Assert.Equal("done", (await w.OkAsync("edit_blog_details", new { title = "Agent's Title" }))["status"]!.GetValue<string>());
        Assert.Equal("Agent's Title", (await BlogSettingsAsync(w))["title"]!.GetValue<string>());

        async Task<JsonNode> Change() => (await w.Owner.GetJsonAsync($"/api/blogs/{blog}/history")).Json!["events"]!.AsArray()
            .First(e => e!["kind"]!.GetValue<string>() == "blog_settings_changed" && e["actor"]!.GetValue<string>().StartsWith("connection:"))!;
        Assert.Null((await Change())["undo"]!["why_not"]);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.PostJsonAsync($"/api/blogs/{blog}/history/{(await Change())["id"]}/undo", null, csrf: true)).Status);
        Assert.Equal(before, (await BlogSettingsAsync(w))["title"]!.GetValue<string>());

        // Changed again after the agent: undoing would overwrite that, so it says so.
        await w.OkAsync("edit_blog_details", new { title = "Agent Again" });
        await w.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{blog}", csrf: true, body: new { title = "Owner's Own" });
        Assert.NotNull((await Change())["undo"]!["why_not"]);
    }

    [Fact]
    public async Task A_picture_that_left_use_turns_its_request_stale_and_only_the_asking_agent_reads_it()
    {
        var (w, _) = await WithPictureAsync();
        await using var _w = w;
        var media = (await w.OkAsync("upload_media_from_url", new { url = "https://pics.example/cat.png", alt = "A cat" }))["media"]!;
        var mediaId = media["id"]!.GetValue<string>();
        var id = (await w.OkAsync("trash_media", new { media_id = mediaId }))["request"]!["id"]!.GetValue<string>();

        // The owner trashes it in the app first: the Inbox says why before they decide, and approving changes nothing.
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostJsonAsync($"/api/media/{mediaId}/trash", null, csrf: true)).Status);
        Assert.Equal("The picture isn't in use any more.", Assert.Single(await WaitingAsync(w.Owner))!["why_not"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(w.Owner, id, "approve")).Status);

        // Another connection of the same owner asks after it: not its request, so not found.
        var other = await ConnectionTests.ConnectAsync(w.Owner, "Second");
        var result = await ConnectionTests.RpcResultAsync(await ConnectionTests.McpAsync(w.Host, other.AccessToken, "tools/call",
            new { name = "get_request", arguments = new { request = id } }));
        Assert.True(result["isError"]?.GetValue<bool>());
        Assert.Equal("not_found", JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_the_owner_decides_and_undoes_and_bad_changes_are_refused()
    {
        await using var c = await PeoplesAgentsTests.Crew.StartAsync();
        var id = (await c.W.OkAsync("edit_blog_details", new { title = "Mine" }))["request"]!["id"]!.GetValue<string>();
        Assert.Empty(await WaitingAsync(c.Ben));
        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync(c.Ben, id, "approve")).Status);

        var (refused, body) = await c.W.ToolAsync("edit_blog_details", new { });
        Assert.True(refused);
        Assert.Equal("invalid_argument", body["code"]!.GetValue<string>());
        Assert.True((await c.W.ToolAsync("edit_blog_details", new { title = new string('x', 201) })).Refused);

        // The core holds deciding and undoing for the owner alone, not only the API.
        var ben = AgentBlog.Posts.Actor.Person(Guid.Parse(c.Blog), Guid.Parse(await c.AccountIdAsync("Ben")), "editor");
        var requests = c.Host.App.Factory.Services.GetRequiredService<AgentBlog.Blogs.BlogRequests>();
        Assert.Equal("not_found", (await requests.DecideAsync(ben, Guid.Parse(id), true, null, default)).Code);
        var undo = c.Host.App.Factory.Services.GetRequiredService<AgentBlog.Posts.Undo>();
        var why = await undo.UndoAsync(ben, 1, default);
        Assert.Equal("Only the owner undoes from History.", why);
    }
}
