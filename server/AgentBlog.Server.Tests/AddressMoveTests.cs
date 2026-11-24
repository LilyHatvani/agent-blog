using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Moving the blog to a new address (docs/specs/address-move.md): <c>Blog__PublicUrl</c> changes on a restart, and
/// <c>Blog__PreviousUrls</c> names the old one. The old address 301s pages and serves nothing else differently; Google,
/// Discord and the trusted provider keep signing people in; passkeys, bound to the old host, are reported as such so
/// the app asks for one here; a passkey-only owner recovers with a new setup token; and a passkey-only writer is
/// invited to claim their account again, under bounds that keep that from being a way into anyone else's.
/// </summary>
public sealed class AddressMoveTests
{
    private const string NewUrl = "https://new.blog.test";
    private static readonly StubAccount Writer2 = new("600000000000000002", "Ben", null);

    private static Action<IWebHostBuilder> Moved(string? previous = SignInHost.PublicUrl) => b =>
    {
        b.UseSetting("Blog:PublicUrl", NewUrl);
        if (previous is not null) b.UseSetting("Blog:PreviousUrls", previous);
    };

    private static async Task<HttpResponseMessage> AtAsync(SignInBrowser browser, HttpMethod method, string path, string host)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Host = host;
        return await browser.Http.SendAsync(request);
    }

    private static async Task<(JsonResult Begin, JsonResult? Finish)> CeremonyAsync(SignInBrowser browser, object begin, Func<JsonNode, JsonObject> answer,
        string beginPath = "/api/auth/passkey/begin", string finishPath = "/api/auth/passkey/finish", bool csrf = false)
    {
        var started = await browser.PostJsonAsync(beginPath, begin, csrf);
        if (started.Status != HttpStatusCode.OK) return (started, null);
        return (started, await browser.PostJsonAsync(finishPath,
            new { ceremony = started.Json!["ceremony"]!.GetValue<string>(), credential = answer(started.Json["options"]!) }, csrf));
    }

    private static Task<(JsonResult Begin, JsonResult? Finish)> AddPasskeyAsync(SignInBrowser browser, SoftAuthenticator key) =>
        CeremonyAsync(browser, new { }, key.Create, "/api/auth/passkeys/begin", "/api/auth/passkeys/finish", csrf: true);

    private static async Task<string> InviteAsync(SignInBrowser owner, string blog, string role = "writer", string? account = null)
    {
        var r = await owner.PostJsonAsync($"/api/blogs/{blog}/invitations", new { role, account }, csrf: true);
        Assert.True(r.Status == HttpStatusCode.Created, r.Json?.ToJsonString());
        var link = r.Json!["link"]!.GetValue<string>();
        return link[(link.LastIndexOf('/') + 1)..];
    }

    private static async Task<JsonObject> MeAsync(SignInBrowser browser) => (await browser.GetJsonAsync("/api/me")).Json!;

    [Fact]
    public async Task The_old_address_answers_pages_with_a_301_to_the_new_one_and_serves_everything_else_as_before()
    {
        await using var host = await SignInHost.StartAsync(configure: Moved($"{SignInHost.PublicUrl}, https://older.test:8443"));
        var owner = host.Browser();
        await owner.SetupAsync(host.SetupToken, "discord", PostTests.Owner);

        foreach (var (method, path, from) in new[]
        {
            (HttpMethod.Get, "/posts/hello?ref=feed", "blog.test"),
            (HttpMethod.Head, "/", "blog.test"),
            (HttpMethod.Get, "/admin", "older.test:8443"),
            (HttpMethod.Get, "/sign-in", "BLOG.TEST"),
            (HttpMethod.Get, "/feed.xml", "blog.test:443"),
            (HttpMethod.Get, "/media/00000000-0000-0000-0000-000000000000/a.png", "blog.test"),
        })
        {
            var r = await AtAsync(host.Browser(), method, path, from);
            Assert.Equal(HttpStatusCode.MovedPermanently, r.StatusCode);
            Assert.Equal(NewUrl + path, r.Headers.Location!.OriginalString);
        }

        // The target is the new address whatever the path says.
        Assert.StartsWith(NewUrl + "/", (await AtAsync(host.Browser(), HttpMethod.Get, "//evil.example/x", "blog.test")).Headers.Location!.OriginalString);

        // Not redirected: the API (a stale tab at the old address keeps its session), the MCP door's discovery, a POST
        // such as a one-click unsubscribe from an email sent before the move, and any host that isn't listed.
        Assert.Equal(HttpStatusCode.OK, (await AtAsync(owner, HttpMethod.Get, "/api/me", "blog.test")).StatusCode);
        Assert.NotEqual(HttpStatusCode.MovedPermanently, (await AtAsync(host.Browser(), HttpMethod.Get, "/.well-known/oauth-protected-resource/mcp", "blog.test")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtAsync(host.Browser(), HttpMethod.Post, $"/unsubscribe?s={Guid.NewGuid()}&t=x", "blog.test")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AtAsync(host.Browser(), HttpMethod.Get, "/", "elsewhere.test")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AtAsync(host.Browser(), HttpMethod.Get, "/", "new.blog.test")).StatusCode);
    }

    [Fact]
    public async Task Without_previous_addresses_no_host_is_redirected()
    {
        await using var host = await SignInHost.StartAsync(configure: Moved(previous: null));
        await host.Browser().SetupAsync(host.SetupToken, "discord", PostTests.Owner);
        Assert.Equal(HttpStatusCode.OK, (await AtAsync(host.Browser(), HttpMethod.Get, "/", "blog.test")).StatusCode);
    }

    [Fact]
    public async Task After_a_move_a_provider_still_signs_people_in_and_whoever_has_only_old_passkeys_is_asked_for_one_here()
    {
        await using var host = await SignInHost.StartAsync();
        var owner = host.Browser();
        await owner.SetupAsync(host.SetupToken, "discord", PostTests.Owner);
        var oldKey = new SoftAuthenticator();
        Assert.Equal(HttpStatusCode.Created, (await AddPasskeyAsync(owner, oldKey)).Finish!.Status);
        var before = await MeAsync(owner);
        Assert.True(before["passkey_here"]!.GetValue<bool>());
        Assert.Equal("blog.test", before["identities"]!.AsArray().Single(i => i!["provider"]!.GetValue<string>() == "passkey")!["made_at"]!.GetValue<string>());

        await using var moved = host.Restart(host.SetupToken, Moved());
        var phone = moved.Browser();
        Assert.Equal("/admin", (await phone.SignInAsync("discord", PostTests.Owner)).Location);
        var me = await MeAsync(phone);
        Assert.Equal("new.blog.test", me["passkey_host"]!.GetValue<string>());
        Assert.False(me["passkey_here"]!.GetValue<bool>());
        Assert.Equal("blog.test", me["identities"]!.AsArray().Single(i => i!["provider"]!.GetValue<string>() == "passkey")!["made_at"]!.GetValue<string>());

        // A passkey made here answers the prompt and signs in on its own.
        var newKey = new SoftAuthenticator(NewUrl);
        var (begin, added) = await AddPasskeyAsync(phone, newKey);
        Assert.Equal("new.blog.test", begin.Json!["options"]!["rp"]!["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Created, added!.Status);
        Assert.True((await MeAsync(phone))["passkey_here"]!.GetValue<bool>());
        var (_, signedIn) = await CeremonyAsync(moved.Browser(), new { purpose = "sign-in" }, newKey.Get);
        Assert.Equal(HttpStatusCode.OK, signedIn!.Status);
    }

    [Fact]
    public async Task A_passkey_only_owner_recovers_at_the_new_address_with_a_new_setup_token_as_the_same_owner()
    {
        await using var host = await SignInHost.StartAsync(google: false, configure: b =>
        {
            b.UseSetting("Blog:Auth:Discord:ClientId", "");
            b.UseSetting("Blog:Auth:Discord:ClientSecret", "");
        });
        var first = host.Browser();
        var (_, claimed) = await CeremonyAsync(first, new { purpose = "setup", token = host.SetupToken, name = "Lenka" }, new SoftAuthenticator().Create);
        Assert.Equal(HttpStatusCode.OK, claimed!.Status);
        var blog = (await MeAsync(first))["blogs"]![0]!["id"]!.GetValue<string>();

        var token = "setup-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));
        await using var moved = host.Restart(token, Moved());
        var phone = moved.Browser();
        var (_, recovered) = await CeremonyAsync(phone, new { purpose = "setup", token, name = "Lenka" }, new SoftAuthenticator(NewUrl).Create);
        Assert.Equal(HttpStatusCode.OK, recovered!.Status);
        var me = await MeAsync(phone);
        Assert.Equal(blog, Assert.Single(me["blogs"]!.AsArray())!["id"]!.GetValue<string>());
        Assert.Equal("owner", me["blogs"]![0]!["role"]!.GetValue<string>());
        Assert.Equal(2, me["identities"]!.AsArray().Count);
        Assert.True(me["passkey_here"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_writer_stranded_by_the_move_claims_their_account_again_from_the_owners_link()
    {
        await using var host = await SignInHost.StartAsync();
        var owner = host.Browser();
        await owner.SetupAsync(host.SetupToken, "discord", PostTests.Owner);
        var blog = (await MeAsync(owner))["blogs"]![0]!["id"]!.GetValue<string>();
        var anna = host.Browser();
        var oldKey = new SoftAuthenticator();
        var (_, joined) = await CeremonyAsync(anna, new { purpose = "invite", token = await InviteAsync(owner, blog), name = "Anna" }, oldKey.Create);
        Assert.Equal(HttpStatusCode.OK, joined!.Status);
        var annaId = (await MeAsync(anna))["account"]!["id"]!.GetValue<string>();
        var draft = await anna.PostJsonAsync($"/api/blogs/{blog}/posts", new { title = "Anna's draft", body_md = "Words." }, csrf: true);
        Assert.Equal(HttpStatusCode.OK, draft.Status);

        await using var moved = host.Restart(host.SetupToken, Moved());
        var owner2 = moved.Browser();
        await owner2.SignInAsync("discord", PostTests.Owner);
        var person = (await owner2.GetJsonAsync($"/api/blogs/{blog}/people")).Json!["people"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "Anna")!;
        Assert.True(person["signed_in"]!.GetValue<bool>());
        Assert.True(person["stranded"]!.GetValue<bool>());

        // The owner's link claims Anna's account: a passkey made here from it is Anna, posts and all.
        var token = await InviteAsync(owner2, blog, account: annaId);
        var staleTab = moved.Browser(anna); // her old session, from before the move
        Assert.Equal(HttpStatusCode.OK, (await staleTab.GetJsonAsync("/api/me")).Status);
        var phone = moved.Browser();
        var newKey = new SoftAuthenticator(NewUrl);
        var (_, back) = await CeremonyAsync(phone, new { purpose = "invite", token, name = "Anna" }, newKey.Create);
        Assert.True(back!.Status == HttpStatusCode.OK, back.Json?.ToJsonString());
        var me = await MeAsync(phone);
        Assert.Equal(annaId, me["account"]!["id"]!.GetValue<string>());
        Assert.Equal("writer", me["blogs"]![0]!["role"]!.GetValue<string>());
        Assert.True(me["passkey_here"]!.GetValue<bool>());
        Assert.Contains((await phone.GetJsonAsync($"/api/blogs/{blog}/posts")).Json!["posts"]!.AsArray(), p => p!["title"]!.GetValue<string>() == "Anna's draft");

        // Her sessions from before ended; the new passkey signs her in by itself, as her.
        Assert.Equal(HttpStatusCode.Unauthorized, (await staleTab.GetJsonAsync("/api/me")).Status);
        var again = moved.Browser();
        var (_, signedIn) = await CeremonyAsync(again, new { purpose = "sign-in" }, newKey.Get);
        Assert.Equal(HttpStatusCode.OK, signedIn!.Status);
        Assert.Equal(annaId, (await MeAsync(again))["account"]!["id"]!.GetValue<string>());

        // Not stranded any more: a second claim is refused, and the history says what happened.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner2.PostJsonAsync($"/api/blogs/{blog}/invitations", new { role = "writer", account = annaId }, csrf: true)).Status);
        await using var conn = new NpgsqlConnection(host.App.AppConnectionString);
        Assert.Contains("\"reclaimed\": true", await conn.QuerySingleAsync<string>(
            "SELECT payload::text FROM event WHERE kind = 'invitation_accepted' ORDER BY id DESC LIMIT 1"));
    }

    [Fact]
    public async Task Claiming_an_account_is_refused_when_it_has_a_way_in_here_is_on_another_blog_or_is_the_owner()
    {
        await using var host = await SignInHost.StartAsync();
        var owner = host.Browser();
        await owner.SetupAsync(host.SetupToken, "discord", PostTests.Owner);
        var ownerId = (await MeAsync(owner))["account"]!["id"]!.GetValue<string>();
        var blog = (await MeAsync(owner))["blogs"]![0]!["id"]!.GetValue<string>();

        // Ben signs in with Discord (not host-bound); Cleo and Dora only with passkeys made here, before the move.
        var ben = host.Browser();
        var benBegin = await ben.PostJsonAsync("/api/auth/invite", new { token = await InviteAsync(owner, blog), provider = "discord" });
        Assert.Equal("/admin?joined=1", (await ben.CompleteAtProviderAsync(new Uri(benBegin.Json!["url"]!.GetValue<string>()), Writer2)).Location);
        var ids = new Dictionary<string, string> { ["Ben"] = (await MeAsync(ben))["account"]!["id"]!.GetValue<string>() };
        foreach (var name in new[] { "Cleo", "Dora" })
        {
            var b = host.Browser();
            var (_, ok) = await CeremonyAsync(b, new { purpose = "invite", token = await InviteAsync(owner, blog), name }, new SoftAuthenticator().Create);
            Assert.Equal(HttpStatusCode.OK, ok!.Status);
            ids[name] = (await MeAsync(b))["account"]!["id"]!.GetValue<string>();
        }

        await using var moved = host.Restart(host.SetupToken, Moved());
        var owner2 = moved.Browser();
        await owner2.SignInAsync("discord", PostTests.Owner);
        var people = (await owner2.GetJsonAsync($"/api/blogs/{blog}/people")).Json!["people"]!.AsArray();
        bool Stranded(string name) => people.Single(p => p!["name"]!.GetValue<string>() == name)!["stranded"]!.GetValue<bool>();
        Assert.False(Stranded("Ben"));
        Assert.True(Stranded("Cleo"));

        Task<JsonResult> ClaimAsync(string account) => owner2.PostJsonAsync($"/api/blogs/{blog}/invitations", new { role = "writer", account }, csrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, (await ClaimAsync(ids["Ben"])).Status); // a way in here: Discord
        Assert.Equal(HttpStatusCode.BadRequest, (await ClaimAsync(ownerId)).Status);    // the owner recovers through setup

        // Cleo is also on another blog of the instance: claiming her account would reach that blog too.
        await using (var conn = new NpgsqlConnection(host.App.AppConnectionString))
        {
            var other = Guid.CreateVersion7();
            await conn.ExecuteAsync("""
                INSERT INTO blog (id, slug, title, created_at, hidden_at) VALUES (@other, 'other-blog', 'Other', now(), now());
                INSERT INTO blog_member (blog_id, account_id, role, created_at) VALUES (@other, @cleo::uuid, 'writer', now());
                """, new { other, cleo = ids["Cleo"] });
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await ClaimAsync(ids["Cleo"])).Status);

        // Dora's link is made while she is claimable; she joins another blog before it's used: it claims nothing.
        var link = await ClaimAsync(ids["Dora"]);
        Assert.Equal(HttpStatusCode.Created, link.Status);
        var token = link.Json!["link"]!.GetValue<string>()[(link.Json["link"]!.GetValue<string>().LastIndexOf('/') + 1)..];
        await using (var conn = new NpgsqlConnection(host.App.AppConnectionString))
        {
            await conn.ExecuteAsync("""
                INSERT INTO blog_member (blog_id, account_id, role, created_at)
                SELECT id, @dora::uuid, 'writer', now() FROM blog WHERE slug = 'other-blog'
                """, new { dora = ids["Dora"] });
        }
        var (_, refused) = await CeremonyAsync(moved.Browser(), new { purpose = "invite", token, name = "Not Dora" }, new SoftAuthenticator(NewUrl).Create);
        Assert.Equal(HttpStatusCode.Gone, refused!.Status);
    }
}
