using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Passkeys (docs/specs/auth.md § Passkeys), with a software authenticator signing real WebAuthn answers: a fresh deploy
/// is claimed with the setup token and a passkey alone, its owner signs in with it, writers accept invitations with one,
/// and anyone adds one to their account. A ceremony answers once, for the blog's own origin, with the passkey it asked.
/// </summary>
public sealed class PasskeyTests
{
    private static readonly StubAccount Lenka = new("400000000000000001", "Lenka", null);

    /// <summary>The system clock moved on by <see cref="Ahead"/>.</summary>
    private sealed class MovingClock : TimeProvider
    {
        public TimeSpan Ahead { get; set; }
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Ahead;
    }

    /// <summary>No Google, no Discord: what a template deploy has when nobody filled anything in.</summary>
    private static Task<SignInHost> BareAsync(TimeProvider? clock = null) => SignInHost.StartAsync(google: false, configure: b =>
    {
        b.UseSetting("Blog:Auth:Discord:ClientId", "");
        b.UseSetting("Blog:Auth:Discord:ClientSecret", "");
        if (clock is not null) b.ConfigureTestServices(s => s.AddSingleton(clock));
    });

    private static async Task<(JsonResult Begin, JsonResult? Finish)> CeremonyAsync(SignInBrowser browser, object begin,
        Func<JsonNode, JsonObject> answer, string beginPath = "/api/auth/passkey/begin", string finishPath = "/api/auth/passkey/finish", bool csrf = false)
    {
        var started = await browser.PostJsonAsync(beginPath, begin, csrf);
        if (started.Status != HttpStatusCode.OK) return (started, null);
        var credential = answer(started.Json!["options"]!);
        return (started, await browser.PostJsonAsync(finishPath, new { ceremony = started.Json["ceremony"]!.GetValue<string>(), credential }, csrf));
    }

    private static Task<(JsonResult Begin, JsonResult? Finish)> SetUpAsync(SignInHost host, SignInBrowser browser, SoftAuthenticator key, string name = "Lenka") =>
        CeremonyAsync(browser, new { purpose = "setup", token = host.SetupToken, name }, key.Create);

    private static Task<(JsonResult Begin, JsonResult? Finish)> SignInAsync(SignInBrowser browser, SoftAuthenticator key, string? returnTo = null) =>
        CeremonyAsync(browser, new { purpose = "sign-in", return_to = returnTo }, key.Get);

    [Fact]
    public async Task A_deploy_with_no_providers_is_claimed_with_the_setup_token_and_a_passkey()
    {
        await using var host = await BareAsync();
        var page = await host.Browser().Http.GetStringAsync("/setup");
        Assert.Contains("data-action=\"passkey-setup\"", page);
        Assert.DoesNotContain("No sign-in provider is configured", page);

        var key = new SoftAuthenticator();
        var browser = host.Browser();
        var (begin, finish) = await SetUpAsync(host, browser, key);
        Assert.Equal("blog.test", begin.Json!["options"]!["rp"]!["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, finish!.Status);
        Assert.Equal("/admin", finish.Json!["redirect"]!.GetValue<string>());

        var me = (await browser.GetJsonAsync("/api/me")).Json!;
        Assert.Equal("Lenka", me["account"]!["display_name"]!.GetValue<string>());
        Assert.Equal("owner", me["blogs"]![0]!["role"]!.GetValue<string>());
        var identity = Assert.Single(me["identities"]!.AsArray())!;
        Assert.Equal("passkey", identity["provider"]!.GetValue<string>());
        Assert.True(identity["this_session"]!.GetValue<bool>());

        // The authenticator knows the account by a handle that isn't its id.
        Assert.DoesNotContain(me["account"]!["id"]!.GetValue<string>(),
            System.Text.Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(key.UserHandle)));
        // Setup is done: a second claim is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await SetUpAsync(host, host.Browser(), new SoftAuthenticator())).Begin.Status);
    }

    [Fact]
    public async Task The_owner_signs_in_with_the_passkey_and_goes_back_where_they_were()
    {
        await using var host = await BareAsync();
        var key = new SoftAuthenticator();
        await SetUpAsync(host, host.Browser(), key);

        var phone = host.Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetJsonAsync("/api/me")).Status);
        var page = await phone.Http.GetStringAsync("/sign-in?returnTo=%2Foauth%2Fconsent%3Fx%3D1");
        Assert.Contains("data-action=\"passkey-sign-in\"", page);
        var (_, finish) = await SignInAsync(phone, key, "/oauth/consent?x=1");
        Assert.True(finish!.Status == HttpStatusCode.OK, finish.Json?.ToJsonString());
        Assert.Equal("/oauth/consent?x=1", finish.Json!["redirect"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await phone.GetJsonAsync("/api/me")).Status);

        // Only paths on this site: anything else goes to the app.
        var (_, elsewhere) = await SignInAsync(host.Browser(), key, "https://evil.test/");
        Assert.Equal("/admin", elsewhere!.Json!["redirect"]!.GetValue<string>());

        // The counter moved with each signature.
        await using var conn = new NpgsqlConnection(host.App.AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT sign_count FROM account_passkey", conn);
        Assert.Equal(2L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_ceremony_answers_once_for_this_blog_with_the_passkey_it_asked_and_nothing_else()
    {
        await using var host = await BareAsync();

        // The wrong setup token, and no name.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Browser().PostJsonAsync("/api/auth/passkey/begin",
            new { purpose = "setup", token = "setup-not-the-token-0000000000000000", name = "x" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Browser().PostJsonAsync("/api/auth/passkey/begin",
            new { purpose = "setup", token = host.SetupToken, name = "  " })).Status);

        // Signed for another site: refused, and nothing was claimed.
        var phished = new SoftAuthenticator("https://evil.test");
        var (_, wrongOrigin) = await SetUpAsync(host, host.Browser(), phished);
        Assert.Equal(HttpStatusCode.BadRequest, wrongOrigin!.Status);
        Assert.Equal("passkey_rejected", wrongOrigin.Json!["code"]!.GetValue<string>());

        // A good answer, then the same answer again: the ceremony is spent.
        var key = new SoftAuthenticator();
        var browser = host.Browser();
        var started = await browser.PostJsonAsync("/api/auth/passkey/begin", new { purpose = "setup", token = host.SetupToken, name = "Lenka" });
        var ceremony = started.Json!["ceremony"]!.GetValue<string>();
        var credential = key.Create(started.Json["options"]!);
        Assert.Equal(HttpStatusCode.OK, (await browser.PostJsonAsync("/api/auth/passkey/finish", new { ceremony, credential })).Status);
        Assert.Equal(HttpStatusCode.Gone, (await host.Browser().PostJsonAsync("/api/auth/passkey/finish", new { ceremony, credential })).Status);

        // A passkey this blog never saw signs in nobody; nor does the right one signing for another site.
        var (_, stranger) = await SignInAsync(host.Browser(), new SoftAuthenticator());
        Assert.Equal(HttpStatusCode.BadRequest, stranger!.Status);
        key.Origin = "https://evil.test";
        var (_, relayed) = await SignInAsync(host.Browser(), key);
        Assert.Equal(HttpStatusCode.BadRequest, relayed!.Status);

        // A sign-in ceremony can't be finished as a setup, nor an "add" without a session.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Browser().PostJsonAsync("/api/auth/passkeys/begin", new { }, csrf: true)).Status);
    }

    [Fact]
    public async Task A_writer_accepts_an_invitation_with_a_passkey_and_loses_it_when_removed()
    {
        await using var host = await BareAsync();
        var owner = host.Browser();
        await SetUpAsync(host, owner, new SoftAuthenticator());
        var blog = (await owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
        var link = (await owner.PostJsonAsync($"/api/blogs/{blog}/invitations", new { role = "writer" }, csrf: true)).Json!["link"]!.GetValue<string>();
        var token = link[(link.LastIndexOf('/') + 1)..];

        var invitePage = await host.Browser().Http.GetStringAsync($"/invite/{token}");
        Assert.Contains("data-action=\"passkey-invite\"", invitePage);

        var anna = host.Browser();
        var annaKey = new SoftAuthenticator();
        var (_, joined) = await CeremonyAsync(anna, new { purpose = "invite", token, name = "Anna" }, annaKey.Create);
        Assert.Equal("/admin?joined=1", joined!.Json!["redirect"]!.GetValue<string>());
        var me = (await anna.GetJsonAsync("/api/me")).Json!;
        Assert.Equal("Anna", me["account"]!["display_name"]!.GetValue<string>());
        Assert.Equal("writer", me["blogs"]![0]!["role"]!.GetValue<string>());

        // Spent: nobody else joins on it.
        Assert.Equal(HttpStatusCode.Gone, (await host.Browser().PostJsonAsync("/api/auth/passkey/begin", new { purpose = "invite", token, name = "Mallory" })).Status);

        // Removed from the blog, her passkey signs in to nothing.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Delete, $"/api/blogs/{blog}/people/{me["account"]!["id"]}", csrf: true)).Status);
        var (_, after) = await SignInAsync(host.Browser(), annaKey);
        Assert.Equal(HttpStatusCode.Forbidden, after!.Status);
    }

    [Fact]
    public async Task Someone_signed_in_with_Discord_adds_a_passkey_signs_in_with_it_and_can_remove_it()
    {
        await using var host = await SignInHost.StartAsync();
        var owner = host.Browser();
        await owner.SetupAsync(host.SetupToken, "discord", Lenka);
        var key = new SoftAuthenticator();
        var (_, added) = await CeremonyAsync(owner, new { }, key.Create, "/api/auth/passkeys/begin", "/api/auth/passkeys/finish", csrf: true);
        Assert.Equal(HttpStatusCode.Created, added!.Status);
        var passkeyId = added.Json!["account_id"]!.GetValue<string>();

        // The write needs the CSRF header like every other.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostJsonAsync("/api/auth/passkeys/begin", new { })).Status);

        var phone = host.Browser();
        var (_, signedIn) = await SignInAsync(phone, key);
        Assert.Equal(HttpStatusCode.OK, signedIn!.Status);
        var me = (await phone.GetJsonAsync("/api/me")).Json!;
        Assert.Equal(2, me["identities"]!.AsArray().Count);

        // Removing the passkey ends what it began, and it no longer signs in.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Delete, $"/api/auth/identities/passkey/{passkeyId}", csrf: true)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetJsonAsync("/api/me")).Status);
        var (_, again) = await SignInAsync(host.Browser(), key);
        Assert.Equal(HttpStatusCode.BadRequest, again!.Status);
    }

    [Fact]
    public async Task A_new_setup_token_lets_the_owner_who_lost_their_passkey_back_in_with_a_new_one()
    {
        await using var host = await BareAsync();
        var lost = new SoftAuthenticator();
        var old = host.Browser();
        await SetUpAsync(host, old, lost);
        var ownerId = (await old.GetJsonAsync("/api/me")).Json!["account"]!["id"]!.GetValue<string>();

        await using var restarted = host.Restart("setup-" + new string('7', 40));
        var found = new SoftAuthenticator();
        var browser = restarted.Browser();
        var (_, back) = await SetUpAsync(restarted, browser, found);
        Assert.Equal(HttpStatusCode.OK, back!.Status);
        var me = (await browser.GetJsonAsync("/api/me")).Json!;
        Assert.Equal(ownerId, me["account"]!["id"]!.GetValue<string>());
        Assert.Equal(2, me["identities"]!.AsArray().Count);
        // Whoever had a session before may be who took the old passkey: it ended.
        Assert.Equal(HttpStatusCode.Unauthorized, (await restarted.Browser(old).GetJsonAsync("/api/me")).Status);
    }

    [Fact]
    public async Task A_ceremony_ends_with_its_five_minutes_and_with_its_setup_token()
    {
        var clock = new MovingClock();
        await using var host = await BareAsync(clock);
        var key = new SoftAuthenticator();
        var browser = host.Browser();
        var late = await browser.PostJsonAsync("/api/auth/passkey/begin", new { purpose = "setup", token = host.SetupToken, name = "Lenka" });
        clock.Ahead = TimeSpan.FromMinutes(6);
        Assert.Equal(HttpStatusCode.Gone, (await browser.PostJsonAsync("/api/auth/passkey/finish",
            new { ceremony = late.Json!["ceremony"]!.GetValue<string>(), credential = key.Create(late.Json["options"]!) })).Status);
        clock.Ahead = TimeSpan.Zero;

        // Begun with one setup token; the server restarts with another before the answer: it claims nothing.
        var begun = await browser.PostJsonAsync("/api/auth/passkey/begin", new { purpose = "setup", token = host.SetupToken, name = "Lenka" });
        await using var restarted = host.Restart("setup-" + new string('5', 40));
        var answer = await restarted.Browser().PostJsonAsync("/api/auth/passkey/finish",
            new { ceremony = begun.Json!["ceremony"]!.GetValue<string>(), credential = new SoftAuthenticator().Create(begun.Json["options"]!) });
        Assert.Equal(HttpStatusCode.Conflict, answer.Status);
        Assert.Equal("setup_closed", answer.Json!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await SetUpAsync(restarted, restarted.Browser(), new SoftAuthenticator())).Finish!.Status);
    }

    [Fact]
    public async Task Adding_a_passkey_finishes_only_in_the_session_that_began_it()
    {
        await using var host = await SignInHost.StartAsync();
        var owner = host.Browser();
        await owner.SetupAsync(host.SetupToken, "discord", Lenka);
        var otherTab = host.Browser();
        await otherTab.SignInAsync("discord", Lenka);
        var key = new SoftAuthenticator();

        var begun = await owner.PostJsonAsync("/api/auth/passkeys/begin", new { }, csrf: true);
        var body = new { ceremony = begun.Json!["ceremony"]!.GetValue<string>(), credential = key.Create(begun.Json["options"]!) };
        // The same account in another session can't finish it, and it's spent by trying.
        Assert.Equal(HttpStatusCode.Gone, (await otherTab.PostJsonAsync("/api/auth/passkeys/finish", body, csrf: true)).Status);
        Assert.Equal(HttpStatusCode.Gone, (await owner.PostJsonAsync("/api/auth/passkeys/finish", body, csrf: true)).Status);

        // Nor can anyone finish one signed out.
        var again = await owner.PostJsonAsync("/api/auth/passkeys/begin", new { }, csrf: true);
        Assert.Equal(HttpStatusCode.Gone, (await host.Browser().PostJsonAsync("/api/auth/passkey/finish",
            new { ceremony = again.Json!["ceremony"]!.GetValue<string>(), credential = key.Create(again.Json["options"]!) })).Status);
        Assert.Single((await owner.GetJsonAsync("/api/me")).Json!["identities"]!.AsArray());
    }

    [Fact]
    public async Task The_passkey_is_for_the_blog_s_own_address_whatever_host_a_request_names()
    {
        await using var host = await BareAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/passkey/begin")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { purpose = "sign-in" }),
        };
        request.Headers.Host = "evil.test";
        var response = await host.Browser().Http.SendAsync(request);
        var options = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["options"]!;
        Assert.Equal("blog.test", options["rpId"]!.GetValue<string>());
    }
}
