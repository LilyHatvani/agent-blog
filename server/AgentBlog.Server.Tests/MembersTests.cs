using System.Net;
using System.Text.Json.Nodes;
using AgentBlog.Notify;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// M2b slice 1 (docs/specs/members.md): writers and editors by the owner's invitation, through the real host and the
/// stub providers. An invitation works once, for one account, for a week; a writer drafts and submits, an editor
/// publishes, trashing and the owner's screens stay the owner's; removing someone keeps what they wrote.
/// </summary>
public sealed class MembersTests
{
    private static readonly StubAccount Anna = new("300000000000000001", "Anna", null);
    private static readonly StubAccount Ben = new("300000000000000002", "Ben", null);
    private static readonly StubAccount Mallory = new("300000000000000003", "Mallory", null);

    private sealed class Team : IAsyncDisposable
    {
        public required PostTests.World W { get; init; }
        public required string Blog { get; init; }
        public SignInBrowser Owner => W.Owner;
        public SignInHost Host => W.Host;

        public static async Task<Team> StartAsync()
        {
            var w = await PostTests.World.StartAsync();
            var blog = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
            return new Team { W = w, Blog = blog };
        }

        /// <summary>The owner makes an invitation; returns its token (the end of the link).</summary>
        public async Task<string> InviteAsync(string role, string note = "")
        {
            var r = await Owner.PostJsonAsync($"/api/blogs/{Blog}/invitations", new { role, note }, csrf: true);
            Assert.True(r.Status == HttpStatusCode.Created, r.Json?.ToJsonString());
            var link = r.Json!["link"]!.GetValue<string>();
            Assert.StartsWith(SignInHost.PublicUrl + "/invite/", link);
            return link[(link.LastIndexOf('/') + 1)..];
        }

        /// <summary>Someone opens the link and signs in with <paramref name="who"/>.</summary>
        public async Task<(SignInBrowser Browser, Outcome Outcome)> AcceptAsync(string token, StubAccount who, SignInBrowser? browser = null)
        {
            browser ??= Host.Browser();
            var begin = await browser.PostJsonAsync("/api/auth/invite", new { token, provider = "discord" });
            Assert.True(begin.Status == HttpStatusCode.OK, begin.Json?.ToJsonString());
            return (browser, await browser.CompleteAtProviderAsync(new Uri(begin.Json!["url"]!.GetValue<string>()), who));
        }

        public async Task<SignInBrowser> JoinAsync(string role, StubAccount who)
        {
            var (browser, outcome) = await AcceptAsync(await InviteAsync(role), who);
            Assert.Equal("/admin?joined=1", outcome.Location);
            return browser;
        }

        public async Task<JsonObject> NewPostAsync(SignInBrowser as_, string title, string body = "Words.")
        {
            var r = await as_.PostJsonAsync($"/api/blogs/{Blog}/posts", new { title, body_md = body }, csrf: true);
            Assert.True(r.Status == HttpStatusCode.OK, r.Json?.ToJsonString());
            return r.Json!["post"]!.AsObject();
        }

        public static Task<JsonResult> ActAsync(SignInBrowser as_, JsonNode post, string op) =>
            as_.PostJsonAsync($"/api/posts/{post["id"]}/{op}", new
            {
                base_revision = post["revision"]!.GetValue<string>(),
                at = op == "schedule" ? DateTimeOffset.UtcNow.AddDays(1) : (DateTimeOffset?)null,
            }, csrf: true);

        public static Task<JsonResult> EditAsync(SignInBrowser as_, JsonNode post, string title) =>
            as_.SendAsync(HttpMethod.Patch, $"/api/posts/{post["id"]}", csrf: true, body: new { base_revision = post["revision"]!.GetValue<string>(), title });

        public async Task<JsonNode> PersonAsync(string name) =>
            (await Owner.GetJsonAsync($"/api/blogs/{Blog}/people")).Json!["people"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == name)!;

        public async Task<T> OwnerSqlAsync<T>(string sql, object? args = null)
        {
            await using var conn = new NpgsqlConnection(Host.App.OwnerConnectionString);
            return await conn.QuerySingleAsync<T>(sql, args);
        }

        public ValueTask DisposeAsync() => W.DisposeAsync();
    }

    // ---------------------------------------------------------------------------------------------- inviting

    [Fact]
    public async Task An_invitation_works_once_for_the_account_that_signs_in_and_names_its_role()
    {
        await using var t = await Team.StartAsync();
        var token = await t.InviteAsync("writer", "Anna, travel posts");

        // The page says who invites whom, as what, without a session.
        var page = await t.Host.Browser().Http.GetStringAsync($"/invite/{token}");
        Assert.Contains("invites you to write for", page);
        Assert.Contains("a writer", page);

        var (anna, joined) = await t.AcceptAsync(token, Anna);
        Assert.Equal("/admin?joined=1", joined.Location);
        Assert.True(joined.SetsSession);
        var blog = Assert.Single((await anna.GetJsonAsync("/api/me")).Json!["blogs"]!.AsArray())!;
        Assert.Equal("writer", blog["role"]!.GetValue<string>());

        // Spent: a forwarded copy gets nobody in, and its page says so.
        var again = await t.Host.Browser().PostJsonAsync("/api/auth/invite", new { token, provider = "discord" });
        Assert.Equal(HttpStatusCode.Gone, again.Status);
        Assert.DoesNotContain("invites you to write for", await t.Host.Browser().Http.GetStringAsync($"/invite/{token}"));

        var screen = (await t.Owner.GetJsonAsync($"/api/blogs/{t.Blog}/people")).Json!;
        var invitation = Assert.Single(screen["invitations"]!.AsArray())!;
        Assert.Equal("accepted", invitation["state"]!.GetValue<string>());
        Assert.Equal("Anna", invitation["used_by"]!.GetValue<string>());
        Assert.Equal(["owner", "writer"], screen["people"]!.AsArray().Select(p => p!["role"]!.GetValue<string>()));
        Assert.Equal(1, await t.OwnerSqlAsync<int>("SELECT count(*)::int FROM event WHERE kind = 'invitation_accepted'"));
    }

    [Fact]
    public async Task Two_people_racing_on_one_link_cannot_both_join()
    {
        await using var t = await Team.StartAsync();
        var token = await t.InviteAsync("editor");
        var early = t.Host.Browser();
        var url = new Uri((await early.PostJsonAsync("/api/auth/invite", new { token, provider = "discord" })).Json!["url"]!.GetValue<string>());

        await t.AcceptAsync(token, Anna);
        var late = await early.CompleteAtProviderAsync(url, Mallory);
        Assert.Equal("/sign-in?error=invitation_gone", late.Location);
        Assert.False(late.SetsSession);
        Assert.Equal(0, await t.OwnerSqlAsync<int>("SELECT count(*)::int FROM account WHERE display_name = 'Mallory'"));
    }

    [Fact]
    public async Task An_expired_or_withdrawn_invitation_lets_nobody_in()
    {
        await using var t = await Team.StartAsync();
        var expired = await t.InviteAsync("writer");
        await t.OwnerSqlAsync<int>("UPDATE invitation SET expires_at = now() - interval '1 minute' RETURNING 1");
        Assert.Equal(HttpStatusCode.Gone, (await t.Host.Browser().PostJsonAsync("/api/auth/invite", new { token = expired, provider = "discord" })).Status);

        // Begun while it worked, finished after it ran out: judged again on the way back, nobody joins.
        var late = await t.InviteAsync("writer");
        var lateBrowser = t.Host.Browser();
        var lateUrl = new Uri((await lateBrowser.PostJsonAsync("/api/auth/invite", new { token = late, provider = "discord" })).Json!["url"]!.GetValue<string>());
        await t.OwnerSqlAsync<int>("UPDATE invitation SET expires_at = now() - interval '1 minute' WHERE expires_at > now() RETURNING 1");
        Assert.Equal("/sign-in?error=invitation_gone", (await lateBrowser.CompleteAtProviderAsync(lateUrl, Ben)).Location);
        Assert.Equal(0, await t.OwnerSqlAsync<int>("SELECT count(*)::int FROM account WHERE display_name = 'Ben'"));

        var withdrawn = await t.InviteAsync("writer");
        var id = (await t.Owner.GetJsonAsync($"/api/blogs/{t.Blog}/people")).Json!["invitations"]!.AsArray()
            .Single(i => i!["state"]!.GetValue<string>() == "pending")!["id"]!.GetValue<string>();
        // Begun before the owner withdrew it, finished after: still nobody.
        var browser = t.Host.Browser();
        var url = new Uri((await browser.PostJsonAsync("/api/auth/invite", new { token = withdrawn, provider = "discord" })).Json!["url"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.PostJsonAsync($"/api/blogs/{t.Blog}/invitations/{id}/withdraw", null, csrf: true)).Status);
        Assert.Equal("/sign-in?error=invitation_gone", (await browser.CompleteAtProviderAsync(url, Anna)).Location);
        Assert.Equal(HttpStatusCode.Gone, (await t.Host.Browser().PostJsonAsync("/api/auth/invite", new { token = withdrawn, provider = "discord" })).Status);
    }

    [Fact]
    public async Task Nobody_gets_in_without_an_invitation_and_a_member_or_the_owner_leaves_a_link_unspent()
    {
        await using var t = await Team.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.Host.Browser().SignInAsync("discord", Mallory)).ApiStatus);

        var token = await t.InviteAsync("writer");
        var (_, owner) = await t.AcceptAsync(token, PostTests.Owner);
        Assert.Equal("/admin", owner.Location); // signed in as the owner they are, nothing joined
        var blog = Assert.Single((await t.Owner.GetJsonAsync("/api/me")).Json!["blogs"]!.AsArray())!;
        Assert.Equal("owner", blog["role"]!.GetValue<string>());

        // The link is still for whoever it was meant for.
        await t.AcceptAsync(token, Anna);
        Assert.Equal("writer", (await t.PersonAsync("Anna"))["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_the_owner_invites_sets_roles_and_removes_and_no_role_reaches_the_owners()
    {
        await using var t = await Team.StartAsync();
        var ben = await t.JoinAsync("editor", Ben);
        var owner = await t.PersonAsync("Lenka");

        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetJsonAsync($"/api/blogs/{t.Blog}/people")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.PostJsonAsync($"/api/blogs/{t.Blog}/invitations", new { role = "editor" }, csrf: true)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.SendAsync(HttpMethod.Delete, $"/api/blogs/{t.Blog}/people/{owner["account"]}", csrf: true)).Status);

        // The owner can't be made anything else, can't be removed, and nobody is invited as an owner.
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{t.Blog}/people/{owner["account"]}", csrf: true, body: new { role = "writer" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/blogs/{t.Blog}/people/{owner["account"]}", csrf: true)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Owner.PostJsonAsync($"/api/blogs/{t.Blog}/invitations", new { role = "owner" }, csrf: true)).Status);
        var benId = (await t.PersonAsync("Ben"))["account"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{t.Blog}/people/{benId}", csrf: true, body: new { role = "owner" })).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{t.Blog}/people/{benId}", csrf: true, body: new { role = "writer" })).Status);
        Assert.Equal("writer", (await t.PersonAsync("Ben"))["role"]!.GetValue<string>());
        Assert.Equal(1, await t.OwnerSqlAsync<int>("SELECT count(*)::int FROM blog_member WHERE role = 'owner'"));
    }

    // ------------------------------------------------------------------------------------------------- roles

    [Fact]
    public async Task A_writer_drafts_edits_their_own_and_submits_but_never_publishes()
    {
        await using var t = await Team.StartAsync();
        var anna = await t.JoinAsync("writer", Anna);
        var mine = await t.NewPostAsync(anna, "Lisbon in rain");
        Assert.Equal("draft", mine["status"]!.GetValue<string>());

        var edited = await Team.EditAsync(anna, mine, "Lisbon in the rain");
        Assert.Equal(HttpStatusCode.OK, edited.Status);
        mine = edited.Json!["post"]!.AsObject();
        var submitted = await Team.ActAsync(anna, mine, "submit");
        Assert.Equal(HttpStatusCode.OK, submitted.Status);
        mine = submitted.Json!["post"]!.AsObject();
        Assert.Equal("review", mine["status"]!.GetValue<string>());

        foreach (var op in new[] { "publish", "schedule", "trash" })
        {
            var refused = await Team.ActAsync(anna, mine, op);
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
            Assert.False(string.IsNullOrWhiteSpace(refused.Json!["detail"]!.GetValue<string>()));
        }
        // Nothing waits in the owner's Inbox: a person is refused with the reason, never turned into a request.
        Assert.Empty((await t.Owner.GetJsonAsync("/api/requests")).Json!["requests"]!.AsArray());

        // Someone else's post: readable, not changeable.
        var owners = await t.NewPostAsync(t.Owner, "The owner's own");
        Assert.Equal(HttpStatusCode.OK, (await anna.GetJsonAsync($"/api/posts/{owners["id"]}")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Team.EditAsync(anna, owners, "Mine now")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Team.ActAsync(anna, owners, "submit")).Status);

        // Once live, the writer's own post is an editor's or the owner's to change.
        var live = (await Team.ActAsync(t.Owner, mine, "publish")).Json!["post"]!;
        var tooLate = await Team.EditAsync(anna, live, "Sneaky change");
        Assert.Equal(HttpStatusCode.Forbidden, tooLate.Status);
        Assert.Contains("editor", tooLate.Json!["detail"]!.GetValue<string>());

        var can = (await anna.GetJsonAsync($"/api/posts/{live["id"]}")).Json!["can"]!;
        Assert.True(can["mine"]!.GetValue<bool>());
        Assert.False(can["edit"]!.GetValue<bool>());
        Assert.False(can["publish"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_editor_publishes_and_edits_any_post_but_trashing_stays_the_owners()
    {
        await using var t = await Team.StartAsync();
        var anna = await t.JoinAsync("writer", Anna);
        var ben = await t.JoinAsync("editor", Ben);
        var post = await t.NewPostAsync(anna, "Porto by night");
        post = (await Team.ActAsync(anna, post, "submit")).Json!["post"]!.AsObject();

        var published = await Team.ActAsync(ben, post, "publish");
        Assert.Equal(HttpStatusCode.OK, published.Status);
        post = published.Json!["post"]!.AsObject();
        Assert.Equal("live", post["status"]!.GetValue<string>());
        var edited = await Team.EditAsync(ben, post, "Porto, by night");
        Assert.Equal(HttpStatusCode.OK, edited.Status);
        post = edited.Json!["post"]!.AsObject();

        Assert.Equal(HttpStatusCode.Forbidden, (await Team.ActAsync(ben, post, "trash")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Team.ActAsync(ben, post, "unpublish")).Status);

        // The byline names each person by their own name.
        var detail = (await t.Owner.GetJsonAsync($"/api/posts/{post["id"]}")).Json!;
        Assert.Contains("Anna", detail["byline"]!["text"]!.GetValue<string>());
        Assert.Contains("Ben", detail["byline"]!["text"]!.GetValue<string>());
        Assert.DoesNotContain("Lenka", detail["byline"]!["text"]!.GetValue<string>());
        Assert.Equal(["Anna", "Ben"], detail["revisions"]!.AsArray().Select(r => r!["by_label"]!.GetValue<string>()).Distinct().Order());
    }

    [Fact]
    public async Task A_schedule_an_editor_set_fires_only_while_they_still_may_publish()
    {
        await using var t = await Team.StartAsync();
        var ben = await t.JoinAsync("editor", Ben);
        var post = await t.NewPostAsync(ben, "Later");
        var at = DateTimeOffset.UtcNow.AddMinutes(5);
        var scheduled = await ben.PostJsonAsync($"/api/posts/{post["id"]}/schedule", new { base_revision = post["revision"]!.GetValue<string>(), at }, csrf: true);
        Assert.Equal(HttpStatusCode.OK, scheduled.Status);

        var benId = (await t.PersonAsync("Ben"))["account"]!.GetValue<string>();
        await t.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{t.Blog}/people/{benId}", csrf: true, body: new { role = "writer" });
        await t.Host.App.Factory.Services.GetRequiredService<AgentBlog.Posts.Scheduler>().RunOnceAsync(at.AddMinutes(1), default);

        Assert.Equal("draft", (await t.Owner.GetJsonAsync($"/api/posts/{post["id"]}")).Json!["post"]!["status"]!.GetValue<string>());
        Assert.Contains("may no longer publish", await t.OwnerSqlAsync<string>("SELECT payload->>'reason' FROM event WHERE kind = 'schedule_dropped'"));
    }

    [Fact]
    public async Task The_owners_screens_answer_not_found_to_an_editor_and_a_writer()
    {
        await using var t = await Team.StartAsync();
        var anna = await t.JoinAsync("writer", Anna);
        var ben = await t.JoinAsync("editor", Ben);

        foreach (var (who, path) in new[]
        {
            (ben, $"/api/blogs/{t.Blog}/people"), (ben, $"/api/blogs/{t.Blog}/notifications"), (ben, $"/api/blogs/{t.Blog}/announce"),
            (ben, $"/api/blogs/{t.Blog}/brief"), (ben, $"/api/blogs/{t.Blog}/cdn"), (ben, $"/api/blogs/{t.Blog}/subscribers"),
            (anna, $"/api/blogs/{t.Blog}/history"), (anna, $"/api/blogs/{t.Blog}/people"), (anna, $"/api/blogs/{t.Blog}/notes"),
        })
            Assert.True((await who.GetJsonAsync(path)).Status == HttpStatusCode.NotFound, path);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.SendAsync(HttpMethod.Patch, $"/api/blogs/{t.Blog}", csrf: true, body: new { title = "Mine" })).Status);
        Assert.Empty((await ben.GetJsonAsync("/api/requests")).Json!["requests"]!.AsArray());
        Assert.Equal(HttpStatusCode.OK, (await ben.GetJsonAsync($"/api/blogs/{t.Blog}/history")).Status);
        Assert.Equal(HttpStatusCode.OK, (await anna.GetJsonAsync($"/api/blogs/{t.Blog}/posts")).Status);
    }

    // ----------------------------------------------------------------------------------------------- removal

    [Fact]
    public async Task Removing_someone_ends_their_access_at_once_and_keeps_everything_they_wrote_under_their_name()
    {
        await using var t = await Team.StartAsync();
        var anna = await t.JoinAsync("writer", Anna);
        var post = await t.NewPostAsync(anna, "Coimbra");
        post = (await Team.ActAsync(anna, post, "submit")).Json!["post"]!.AsObject();
        await Team.ActAsync(t.Owner, post, "publish");

        var annaId = (await t.PersonAsync("Anna"))["account"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/blogs/{t.Blog}/people/{annaId}", csrf: true)).Status);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anna.GetJsonAsync("/api/me")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.Host.Browser().SignInAsync("discord", Anna)).ApiStatus);

        var detail = (await t.Owner.GetJsonAsync($"/api/posts/{post["id"]}")).Json!;
        Assert.All(detail["revisions"]!.AsArray(), r => Assert.Equal("Anna", r!["by_label"]!.GetValue<string>()));
        Assert.StartsWith("By Anna", detail["byline"]!["text"]!.GetValue<string>());
        Assert.Contains("Coimbra", await t.Host.Browser().Http.GetStringAsync($"/posts/{post["slug"]}"));
        Assert.Equal(1, await t.OwnerSqlAsync<int>("SELECT count(*)::int FROM event WHERE kind = 'member_removed'"));

        // Invited again, she is back with the same account and her name on the same work.
        await t.JoinAsync("writer", Anna);
        Assert.Equal(annaId, (await t.PersonAsync("Anna"))["account"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------------------------------- notifications

    [Fact]
    public async Task What_a_writer_or_editor_does_reaches_the_owner_and_what_the_owner_does_does_not()
    {
        await using var t = await Team.StartAsync();
        await t.OwnerSqlAsync<int>("""
            INSERT INTO channel (id, blog_id, kind, label, events, detail, created_by, created_at)
            VALUES (gen_random_uuid(), @blog::uuid, 'ntfy', 'Phone', ARRAY['post_submitted', 'post_published', 'invitation_accepted'], 'full', (SELECT account_id FROM blog_member WHERE role = 'owner'), now() - interval '1 hour')
            RETURNING 1
            """, new { blog = t.Blog });
        var anna = await t.JoinAsync("writer", Anna);
        var ben = await t.JoinAsync("editor", Ben);
        var post = (await Team.ActAsync(anna, await t.NewPostAsync(anna, "Braga"), "submit")).Json!["post"]!;
        await Team.ActAsync(ben, post, "publish");
        var own = await t.NewPostAsync(t.Owner, "The owner's");
        await Team.ActAsync(t.Owner, own, "publish");

        await t.Host.App.Factory.Services.GetRequiredService<Notifier>().FanOutAsync(default);
        await using var conn = new NpgsqlConnection(t.Host.App.OwnerConnectionString);
        var queued = (await conn.QueryAsync<(string Kind, string Actor)>(
            "SELECT e.kind, actor_label(e.actor) FROM delivery d JOIN event e ON e.id = d.event_id ORDER BY e.id")).ToList();
        Assert.Equal([("invitation_accepted", "Anna"), ("invitation_accepted", "Ben"), ("post_submitted", "Anna"), ("post_published", "Ben")], queued);
    }

    [Fact]
    public void The_owner_is_never_told_what_the_owner_did()
    {
        const string owner = "account:00000000-0000-0000-0000-000000000001";
        Assert.False(NotifyCatalog.Sendable("post_published", owner, owner));
        Assert.True(NotifyCatalog.Sendable("post_published", "account:00000000-0000-0000-0000-000000000002", owner));
        Assert.True(NotifyCatalog.Sendable("post_submitted", "connection:00000000-0000-0000-0000-000000000003", owner));
    }
}
