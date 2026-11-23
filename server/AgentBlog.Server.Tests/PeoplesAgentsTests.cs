using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// M2b slice 2 (docs/specs/members.md § Their agents): a writer or an editor connects their own agent. It never does more
/// than its person may (a writer's agent stays at Propose and changes only the writer's posts; an editor's goes at most to
/// Publish), judged on every call; only the owner sets levels; requests go to whoever may decide them, and an editor's
/// note never reaches an agent as the owner's words.
/// </summary>
public sealed class PeoplesAgentsTests
{
    private static readonly StubAccount AnnaAccount = new("400000000000000001", "Anna", null);
    private static readonly StubAccount BenAccount = new("400000000000000002", "Ben", null);

    internal sealed class Crew : IAsyncDisposable
    {
        public required PostTests.World W { get; init; }
        public required string Blog { get; init; }
        public required SignInBrowser Anna { get; init; }
        public required SignInBrowser Ben { get; init; }
        public SignInBrowser Owner => W.Owner;
        public SignInHost Host => W.Host;

        /// <summary>The owner (with an agent of their own, Claude), Anna a writer, Ben an editor.</summary>
        public static async Task<Crew> StartAsync()
        {
            var w = await PostTests.World.StartAsync();
            var blog = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
            return new Crew
            {
                W = w, Blog = blog,
                Anna = await JoinAsync(w, blog, "writer", AnnaAccount),
                Ben = await JoinAsync(w, blog, "editor", BenAccount),
            };
        }

        /// <summary>Someone the owner invites, signed in from the link.</summary>
        public Task<SignInBrowser> JoinAsync(string role, StubAccount who) => JoinAsync(W, Blog, role, who);

        private static async Task<SignInBrowser> JoinAsync(PostTests.World w, string blog, string role, StubAccount who)
        {
            var r = await w.Owner.PostJsonAsync($"/api/blogs/{blog}/invitations", new { role }, csrf: true);
            var link = r.Json!["link"]!.GetValue<string>();
            var browser = w.Host.Browser();
            var begin = await browser.PostJsonAsync("/api/auth/invite", new { token = link[(link.LastIndexOf('/') + 1)..], provider = "discord" });
            Assert.Equal("/admin?joined=1", (await browser.CompleteAtProviderAsync(new Uri(begin.Json!["url"]!.GetValue<string>()), who)).Location);
            return browser;
        }

        public Task<ConnectionTests.Connected> ConnectAsync(SignInBrowser who, string label) => ConnectionTests.ConnectAsync(who, label);

        /// <summary>A tool call: whether it was refused, and its JSON.</summary>
        public async Task<(bool Refused, JsonObject Body)> ToolAsync(ConnectionTests.Connected agent, string tool, object? arguments = null)
        {
            var result = await ConnectionTests.RpcResultAsync(
                await ConnectionTests.McpAsync(Host, agent.AccessToken, "tools/call", new { name = tool, arguments = arguments ?? new { } }));
            return (result["isError"]?.GetValue<bool>() == true, JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.AsObject());
        }

        public async Task<JsonObject> OkAsync(ConnectionTests.Connected agent, string tool, object? arguments = null)
        {
            var (refused, body) = await ToolAsync(agent, tool, arguments);
            Assert.False(refused, body.ToJsonString());
            return body;
        }

        public async Task<JsonObject> DraftAsync(ConnectionTests.Connected agent, string title) =>
            (await OkAsync(agent, "create_draft", new { title, body_md = "Words." }))["post"]!.AsObject();

        public async Task<JsonObject> PersonDraftAsync(SignInBrowser who, string title) =>
            (await who.PostJsonAsync($"/api/blogs/{Blog}/posts", new { title, body_md = "Words." }, csrf: true)).Json!["post"]!.AsObject();

        public Task<JsonResult> SetLevelAsync(SignInBrowser who, ConnectionTests.Connected agent, string level) =>
            who.SendAsync(HttpMethod.Patch, $"/api/connections/{agent.Id}", csrf: true, body: new { level, confirm_full = true });

        public async Task<string> AccountIdAsync(string name) =>
            (await Owner.GetJsonAsync($"/api/blogs/{Blog}/people")).Json!["people"]!.AsArray()
                .Single(p => p!["name"]!.GetValue<string>() == name)!["account"]!.GetValue<string>();

        public static async Task<List<string>> WaitingIdsAsync(SignInBrowser who) =>
            [.. (await who.GetJsonAsync("/api/requests")).Json!["requests"]!.AsArray().Select(q => q!["id"]!.GetValue<string>())];

        public ValueTask DisposeAsync() => W.DisposeAsync();
    }

    private static string Rev(JsonNode post) => post["revision"]!.GetValue<string>();

    // ----------------------------------------------------------------------------------------- a writer's agent

    [Fact]
    public async Task A_writers_agent_drafts_and_submits_only_its_writers_posts_and_its_publish_waits()
    {
        await using var c = await Crew.StartAsync();
        var agent = await c.ConnectAsync(c.Anna, "Anna's Claude");

        var blog = await c.OkAsync(agent, "get_blog");
        Assert.Equal("propose", blog["your_level"]!.GetValue<string>());
        Assert.Equal("Anna", blog["your_person"]!["name"]!.GetValue<string>());
        Assert.Equal("writer", blog["your_person"]!["role"]!.GetValue<string>());
        Assert.Equal("propose", blog["your_person"]!["held_to"]!.GetValue<string>());

        // Its own draft and Anna's: it edits and submits both.
        var own = await c.DraftAsync(agent, "Rain in Lisbon");
        var annas = await c.PersonDraftAsync(c.Anna, "Anna's notes");
        Assert.Equal("done", (await c.OkAsync(agent, "edit_post", new { post = annas["id"]!.ToString(), base_revision = Rev(annas), title = "Anna's notes, tidied" }))["status"]!.GetValue<string>());
        var submitted = await c.OkAsync(agent, "submit_for_review", new { post = own["id"]!.ToString() });
        Assert.Equal("review", submitted["post"]!["status"]!.GetValue<string>());
        // And Anna, in the app, edits what her agent started: it's hers.
        Assert.True((await c.Anna.GetJsonAsync($"/api/posts/{own["id"]}")).Json!["can"]!["mine"]!.GetValue<bool>());

        // Someone else's post is out of its reach, as it is out of Anna's.
        var owners = await c.PersonDraftAsync(c.Owner, "The owner's");
        var (refused, why) = await c.ToolAsync(agent, "edit_post", new { post = owners["id"]!.ToString(), base_revision = Rev(owners), title = "Mine now" });
        Assert.True(refused);
        Assert.Equal("forbidden", why["code"]!.GetValue<string>());
        Assert.Contains("writer's agent", why["message"]!.GetValue<string>());

        // Publishing its own waits, for the owner or an editor; never for Anna.
        var asked = await c.OkAsync(agent, "publish_post", new { post = own["id"]!.ToString() });
        Assert.Equal("waiting_for_owner", asked["status"]!.GetValue<string>());
        var request = asked["request"]!["id"]!.GetValue<string>();
        Assert.Contains(request, await Crew.WaitingIdsAsync(c.Owner));
        Assert.Contains(request, await Crew.WaitingIdsAsync(c.Ben));
        Assert.Empty(await Crew.WaitingIdsAsync(c.Anna));
        Assert.Equal(HttpStatusCode.NotFound, (await c.Anna.PostJsonAsync($"/api/requests/{request}/approve", null, csrf: true)).Status);
        var inbox = (await c.Owner.GetJsonAsync("/api/requests")).Json!["requests"]![0]!;
        Assert.Equal("Anna", inbox["agent_person"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_the_owner_sets_a_level_and_never_above_what_the_agents_person_may_do()
    {
        await using var c = await Crew.StartAsync();
        var annas = await c.ConnectAsync(c.Anna, "Anna's Claude");
        var bens = await c.ConnectAsync(c.Ben, "Ben's Claude");

        Assert.Equal(HttpStatusCode.BadRequest, (await c.SetLevelAsync(c.Owner, annas, "publish")).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await c.SetLevelAsync(c.Owner, bens, "publish")).Status);
        var full = await c.SetLevelAsync(c.Owner, bens, "full");
        Assert.Equal(HttpStatusCode.BadRequest, full.Status);
        Assert.Equal("above_person", full.Json!["code"]!.GetValue<string>());

        // Their own agents: listed, renamed, never levelled by them.
        Assert.Equal(HttpStatusCode.NotFound, (await c.SetLevelAsync(c.Ben, bens, "propose")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SetLevelAsync(c.Anna, annas, "propose")).Status);
        var bensList = (await c.Ben.GetJsonAsync("/api/connections")).Json!["connections"]!.AsArray();
        var mine = Assert.Single(bensList)!;
        Assert.Equal("Ben's Claude", mine["label"]!.GetValue<string>());
        Assert.False(mine["can_set_level"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NoContent, (await c.Ben.SendAsync(HttpMethod.Patch, $"/api/connections/{bens.Id}", csrf: true, body: new { byline_name = "Ben's helper" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Ben.SendAsync(HttpMethod.Patch, $"/api/connections/{annas.Id}", csrf: true, body: new { byline_name = "Hijack" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Ben.SendAsync(HttpMethod.Delete, $"/api/connections/{c.W.Agent.Id}", csrf: true)).Status);

        // The owner sees every agent and whose it is.
        var all = (await c.Owner.GetJsonAsync("/api/connections")).Json!["connections"]!.AsArray();
        Assert.Equal(3, all.Count);
        var annasRow = all.Single(x => x!["id"]!.GetValue<string>() == annas.Id.ToString())!;
        Assert.Equal("Anna", annasRow["person"]!["name"]!.GetValue<string>());
        Assert.Equal("propose", annasRow["cap"]!.GetValue<string>());
        Assert.True(annasRow["can_set_level"]!.GetValue<bool>());

        // A person revokes their own agent; its next call finds nothing.
        Assert.Equal(HttpStatusCode.NoContent, (await c.Anna.SendAsync(HttpMethod.Delete, $"/api/connections/{annas.Id}", csrf: true)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ConnectionTests.McpAsync(c.Host, annas.AccessToken, "tools/list")).StatusCode);
    }

    // ----------------------------------------------------------------------------------------- an editor's agent

    [Fact]
    public async Task An_editors_agent_publishes_at_Publish_and_its_trash_request_waits_for_the_owner_alone()
    {
        await using var c = await Crew.StartAsync();
        var agent = await c.ConnectAsync(c.Ben, "Ben's Claude");
        await c.SetLevelAsync(c.Owner, agent, "publish");

        var post = await c.DraftAsync(agent, "Porto");
        var live = await c.OkAsync(agent, "publish_post", new { post = post["id"]!.ToString(), base_revision = Rev(post) });
        Assert.Equal("live", live["post"]!["status"]!.GetValue<string>());

        var trash = await c.OkAsync(agent, "trash_post", new { post = post["id"]!.ToString() });
        Assert.Equal("waiting_for_owner", trash["status"]!.GetValue<string>());
        var request = trash["request"]!["id"]!.GetValue<string>();
        Assert.DoesNotContain(request, await Crew.WaitingIdsAsync(c.Ben));
        Assert.Equal(HttpStatusCode.NotFound, (await c.Ben.PostJsonAsync($"/api/requests/{request}/approve", null, csrf: true)).Status);
        Assert.Contains(request, await Crew.WaitingIdsAsync(c.Owner));
    }

    [Fact]
    public async Task An_editor_decides_an_agents_request_and_their_note_reaches_the_agent_as_an_editors()
    {
        await using var c = await Crew.StartAsync();
        var agent = await c.ConnectAsync(c.Anna, "Anna's Claude");
        var post = await c.DraftAsync(agent, "Braga");
        var first = (await c.OkAsync(agent, "publish_post", new { post = post["id"]!.ToString() }))["request"]!["id"]!.GetValue<string>();

        var changes = await c.Ben.PostJsonAsync($"/api/requests/{first}/changes", new { note = "Name the bakery." }, csrf: true);
        Assert.Equal(HttpStatusCode.OK, changes.Status);
        var seen = (await c.OkAsync(agent, "get_request", new { request = first }))["request"] ?? await c.OkAsync(agent, "get_request", new { request = first });
        var note = seen["owner_note"]!;
        Assert.Equal("editor", note["author"]!.GetValue<string>());
        Assert.Equal("Ben", note["author_name"]!.GetValue<string>());
        Assert.Contains("Ben", seen["next"]!.GetValue<string>());
        var notes = (await c.OkAsync(agent, "get_blog"))["owner_notes"]!.AsArray();
        Assert.All(notes, n => Assert.Equal("editor", n!["author"]!.GetValue<string>()));

        // Asked again, approved by the editor: it goes live, and History names Ben.
        var again = (await c.OkAsync(agent, "publish_post", new { post = post["id"]!.ToString() }))["request"]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await c.Ben.PostJsonAsync($"/api/requests/{again}/approve", null, csrf: true)).Status);
        Assert.Equal("live", (await c.Owner.GetJsonAsync($"/api/posts/{post["id"]}")).Json!["post"]!["status"]!.GetValue<string>());
        await using var conn = new NpgsqlConnection(c.Host.App.OwnerConnectionString);
        Assert.Equal("Ben", await conn.QuerySingleAsync<string>("SELECT actor_label(actor) FROM event WHERE kind = 'approval_approved'"));

        // The owner's own note still comes as the owner's.
        var owners = (await c.OkAsync(c.W.Agent, "publish_post", new { post = (await c.DraftAsync(c.W.Agent, "Faro"))["id"]!.ToString() }))["request"]!["id"]!.GetValue<string>();
        await c.Owner.PostJsonAsync($"/api/requests/{owners}/decline", new { note = "Not this week." }, csrf: true);
        var declined = await c.OkAsync(c.W.Agent, "get_request", new { request = owners });
        var ownerNote = (declined["request"] ?? declined)["owner_note"]!;
        Assert.Equal("owner", ownerNote["author"]!.GetValue<string>());
    }

    // --------------------------------------------------------------------------------- judged on every call

    [Fact]
    public async Task A_role_change_holds_the_agent_to_its_persons_new_reach_from_its_next_call_and_at_its_schedule()
    {
        await using var c = await Crew.StartAsync();
        var agent = await c.ConnectAsync(c.Ben, "Ben's Claude");
        await c.SetLevelAsync(c.Owner, agent, "publish");
        var post = await c.DraftAsync(agent, "Later");
        var at = DateTimeOffset.UtcNow.AddMinutes(10);
        Assert.Equal("done", (await c.OkAsync(agent, "schedule_post", new { post = post["id"]!.ToString(), at = at.ToString("O") }))["status"]!.GetValue<string>());

        var benId = await c.AccountIdAsync("Ben");
        Assert.Equal(HttpStatusCode.NoContent, (await c.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{c.Blog}/people/{benId}", csrf: true, body: new { role = "writer" })).Status);

        var blog = await c.OkAsync(agent, "get_blog");
        Assert.Equal("propose", blog["your_level"]!.GetValue<string>());
        var next = await c.DraftAsync(agent, "Now");
        Assert.Equal("waiting_for_owner", (await c.OkAsync(agent, "publish_post", new { post = next["id"]!.ToString() }))["status"]!.GetValue<string>());
        var row = (await c.Owner.GetJsonAsync("/api/connections")).Json!["connections"]!.AsArray().Single(x => x!["id"]!.GetValue<string>() == agent.Id.ToString())!;
        Assert.Equal("publish", row["level"]!.GetValue<string>());
        Assert.Equal("propose", row["works_at"]!.GetValue<string>());

        await c.Host.App.Factory.Services.GetRequiredService<AgentBlog.Posts.Scheduler>().RunOnceAsync(at.AddMinutes(1), default);
        Assert.Equal("draft", (await c.Owner.GetJsonAsync($"/api/posts/{post["id"]}")).Json!["post"]!["status"]!.GetValue<string>());
        await using var conn = new NpgsqlConnection(c.Host.App.OwnerConnectionString);
        Assert.Contains("works for a writer", await conn.QuerySingleAsync<string>("SELECT payload->>'reason' FROM event WHERE kind = 'schedule_dropped'"));

        // Made an editor again, the level the owner set comes back.
        await c.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{c.Blog}/people/{benId}", csrf: true, body: new { role = "editor" });
        Assert.Equal("publish", (await c.OkAsync(agent, "get_blog"))["your_level"]!.GetValue<string>());
    }

    [Fact]
    public async Task Removing_a_person_revokes_their_agents_and_keeps_what_the_agents_wrote()
    {
        await using var c = await Crew.StartAsync();
        var agent = await c.ConnectAsync(c.Anna, "Anna's Claude");
        var post = await c.DraftAsync(agent, "Évora");

        var annaId = await c.AccountIdAsync("Anna");
        Assert.Equal(HttpStatusCode.NoContent, (await c.Owner.SendAsync(HttpMethod.Delete, $"/api/blogs/{c.Blog}/people/{annaId}", csrf: true)).Status);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ConnectionTests.McpAsync(c.Host, agent.AccessToken, "tools/list")).StatusCode);
        Assert.DoesNotContain((await c.Owner.GetJsonAsync("/api/connections")).Json!["connections"]!.AsArray(),
            x => x!["id"]!.GetValue<string>() == agent.Id.ToString());
        var detail = (await c.Owner.GetJsonAsync($"/api/posts/{post["id"]}")).Json!;
        Assert.Equal("Anna's Claude", detail["revisions"]![0]!["by_label"]!.GetValue<string>());
        await using var conn = new NpgsqlConnection(c.Host.App.OwnerConnectionString);
        Assert.Equal("person_removed", await conn.QuerySingleAsync<string>(
            "SELECT payload->>'why' FROM event WHERE kind = 'connection_revoked' AND target_id = @id", new { id = agent.Id }));
    }
}
