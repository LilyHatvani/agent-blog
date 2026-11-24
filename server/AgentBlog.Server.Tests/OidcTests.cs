using System.Net;
using AgentBlog.Auth;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// A trusted sign-in provider (docs/specs/auth.md § A trusted provider) through the real host and an OpenID Connect
/// provider in the test process: the owner sets it, a member links it and signs in with it, an invitation is claimed with
/// it, and every way an answer can be wrong is refused with its own reason. The untweaked sign-in in each test is the
/// positive control: the same flow, nothing bent, gets in.
/// </summary>
public sealed class OidcTests
{
    private const string MasterKey = "0000000000000000000000000000000000000000000000000000000000000005";
    private static readonly StubAccount Member = new("eod-user-1", "Mira", "mira@example.com");
    private static readonly StubAccount SameEmail = new("eod-user-2", "Not Mira", "mira@example.com");

    internal sealed class World : IAsyncDisposable
    {
        public required SignInHost Host { get; init; }
        public required OidcStub Stub { get; init; }
        public required OidcStub Other { get; init; }
        public required SignInBrowser Owner { get; init; }
        public required string Blog { get; init; }

        public static async Task<World> StartAsync(bool setProvider = true, Action<IWebHostBuilder>? configure = null)
        {
            var stub = new OidcStub("id.example");
            var other = new OidcStub("id2.example");
            var host = await SignInHost.StartAsync(configure: b =>
            {
                b.UseSetting("Blog:Secrets:MasterKey", MasterKey);
                b.ConfigureTestServices(s => s.AddHttpClient(OidcSignIn.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new OidcTransport([stub, other])));
                configure?.Invoke(b);
            });
            var owner = host.Browser();
            await owner.SetupAsync(host.SetupToken, "discord", PostTests.Owner);
            var blog = (await owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
            var w = new World { Host = host, Stub = stub, Other = other, Owner = owner, Blog = blog };
            if (setProvider)
            {
                var r = await w.SetProviderAsync(stub.Issuer);
                Assert.True(r.Status == HttpStatusCode.OK, r.Json?.ToJsonString());
            }
            return w;
        }

        public Task<JsonResult> SetProviderAsync(string issuer, string? secret = OidcStub.ClientSecret, string name = "Echo Test", string clientId = OidcStub.ClientId) =>
            Owner.SendAsync(HttpMethod.Put, $"/api/blogs/{Blog}/sign-in/oidc", csrf: true,
                body: new { issuer, client_id = clientId, client_secret = secret, display_name = name });

        /// <summary>A plain sign-in through the provider: start, the provider (as <paramref name="who"/>), the callback.</summary>
        public async Task<Outcome> SignInAsync(SignInBrowser browser, StubAccount who, IdTokenTweak? tweak = null, OidcStub? at = null)
        {
            var authorize = await StartAsync(browser);
            return await BackAsync(browser, (at ?? Stub).Approve(authorize, who, tweak));
        }

        public async Task<Uri> StartAsync(SignInBrowser browser)
        {
            var start = await browser.Http.GetAsync("/api/auth/oidc/start");
            Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
            return start.Headers.Location!;
        }

        /// <summary>The provider sends the browser back to <paramref name="callback"/>; then /api/me tells whether it's signed in.</summary>
        public static async Task<Outcome> BackAsync(SignInBrowser browser, Uri callback)
        {
            var back = await browser.Http.GetAsync(SignInBrowser.Local(callback));
            Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
            var session = back.Headers.TryGetValues("Set-Cookie", out var set)
                ? set.FirstOrDefault(c => c.StartsWith("blog_session=", StringComparison.Ordinal))
                : null;
            return new Outcome(back.Headers.Location!.OriginalString, session is not null, session, (await browser.Http.GetAsync("/api/me")).StatusCode);
        }

        /// <summary>The owner links <paramref name="who"/>'s account at the provider to their own, from Account.</summary>
        public async Task<Outcome> BindAsync(SignInBrowser browser, StubAccount who, IdTokenTweak? tweak = null)
        {
            var begin = await browser.PostJsonAsync("/api/auth/identities/oidc", null, csrf: true);
            Assert.True(begin.Status == HttpStatusCode.OK, begin.Json?.ToJsonString());
            return await BackAsync(browser, Stub.Approve(new Uri(begin.Json!["url"]!.GetValue<string>()), who, tweak));
        }

        public async Task<string> EventsJsonAsync()
        {
            await using var conn = new NpgsqlConnection(Host.App.AppConnectionString);
            return string.Join('\n', await conn.QueryAsync<string>("SELECT kind || ' ' || coalesce(payload::text, '') FROM event ORDER BY id"));
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Stub.Dispose();
            Other.Dispose();
        }
    }

    [Fact]
    public async Task The_owner_adds_a_provider_links_it_and_signs_in_with_it_keyed_by_issuer_and_sub()
    {
        await using var w = await World.StartAsync();

        // The setting reads back without its secret, with the address to register at the provider.
        var setting = (await w.Owner.GetJsonAsync($"/api/blogs/{w.Blog}/sign-in/oidc")).Json!;
        Assert.True(setting["set"]!.GetValue<bool>());
        Assert.True(setting["secret_set"]!.GetValue<bool>());
        Assert.Equal(SignInHost.PublicUrl + "/api/auth/oidc/callback", setting["redirect_uri"]!.GetValue<string>());
        Assert.DoesNotContain(OidcStub.ClientSecret, setting.ToJsonString());
        Assert.DoesNotContain(OidcStub.ClientSecret, await w.EventsJsonAsync());

        // The sign-in page offers it by its name.
        var page = await (await w.Host.Browser().Http.GetAsync("/sign-in")).Content.ReadAsStringAsync();
        Assert.Contains("href=\"/api/auth/oidc/start\">Sign in with Echo Test</a>", page);

        // Before it's linked, the provider's account is nobody here.
        Assert.Equal("/sign-in?error=not_owner", (await w.SignInAsync(w.Host.Browser(), Member)).Location);

        // The owner links it from Account; then a fresh browser signs in with it alone.
        Assert.Equal("/admin?bound=oidc", (await w.BindAsync(w.Owner, Member)).Location);
        var phone = w.Host.Browser();
        var signedIn = await w.SignInAsync(phone, Member);
        Assert.Equal("/admin", signedIn.Location);
        Assert.Equal(HttpStatusCode.OK, signedIn.ApiStatus);
        var me = (await phone.GetJsonAsync("/api/me")).Json!;
        var way = me["identities"]!.AsArray().Single(i => i!["provider"]!.GetValue<string>() == "oidc")!;
        Assert.Equal(OidcSignIn.AccountKey(w.Stub.Issuer, Member.Id), way["account_id"]!.GetValue<string>());
        Assert.True(way["this_session"]!.GetValue<bool>());
        Assert.Contains("oidc", me["providers"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Equal("Echo Test", me["provider_labels"]!["oidc"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_invitation_is_claimed_with_the_provider()
    {
        await using var w = await World.StartAsync();
        var invited = await w.Owner.PostJsonAsync($"/api/blogs/{w.Blog}/invitations", new { role = "writer" }, csrf: true);
        var link = invited.Json!["link"]!.GetValue<string>();
        var token = link[(link.LastIndexOf('/') + 1)..];
        Assert.Contains("data-provider=\"oidc\">Sign in with Echo Test</button>",
            await (await w.Host.Browser().Http.GetAsync("/invite/" + token)).Content.ReadAsStringAsync());

        var writer = w.Host.Browser();
        var begin = await writer.PostJsonAsync("/api/auth/invite", new { token, provider = "oidc" });
        Assert.True(begin.Status == HttpStatusCode.OK, begin.Json?.ToJsonString());
        var joined = await World.BackAsync(writer, w.Stub.Approve(new Uri(begin.Json!["url"]!.GetValue<string>()), Member));
        Assert.Equal("/admin?joined=1", joined.Location);
        var me = (await writer.GetJsonAsync("/api/me")).Json!;
        Assert.Equal("writer", me["blogs"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("Mira", me["account"]!["display_name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("signature", "oidc_signature")]
    [InlineData("audience", "oidc_audience")]
    [InlineData("expired", "oidc_expired")]
    [InlineData("issuer", "oidc_issuer")]
    [InlineData("no-nonce", "oidc_nonce")]
    [InlineData("no-id-token", "oidc_rejected")]
    public async Task A_bent_ID_token_is_refused_with_its_own_reason_and_signs_nobody_in(string bent, string code)
    {
        await using var w = await World.StartAsync();
        await w.BindAsync(w.Owner, Member);
        var tweak = bent switch
        {
            "signature" => new IdTokenTweak(ForgedSignature: true),
            "audience" => new IdTokenTweak(Audience: "someone-elses-app"),
            "expired" => new IdTokenTweak(Expired: true),
            "issuer" => new IdTokenTweak(Issuer: "https://evil.example"),
            "no-nonce" => new IdTokenTweak(OmitNonce: true),
            _ => new IdTokenTweak(OmitIdToken: true),
        };

        var refused = await w.SignInAsync(w.Host.Browser(), Member, tweak);
        Assert.Equal("/sign-in?error=" + code, refused.Location);
        Assert.False(refused.SetsSession);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.ApiStatus);
        // The page names the reason in words, never the code.
        var page = await (await w.Host.Browser().Http.GetAsync(refused.Location)).Content.ReadAsStringAsync();
        Assert.Contains("class=\"error\"", page);
        Assert.DoesNotContain(code, page);
        Assert.DoesNotContain("Something went wrong", page);

        // The positive control: the same member, nothing bent, gets in.
        Assert.Equal("/admin", (await w.SignInAsync(w.Host.Browser(), Member)).Location);
    }

    [Fact]
    public async Task A_replayed_nonce_or_callback_is_refused()
    {
        await using var w = await World.StartAsync();
        await w.BindAsync(w.Owner, Member);

        // An earlier sign-in's nonce in this sign-in's token.
        var first = w.Host.Browser();
        var authorize = await w.StartAsync(first);
        var callback = w.Stub.Approve(authorize, Member);
        Assert.Equal("/admin", (await World.BackAsync(first, callback)).Location);
        var earlierNonce = w.Stub.Nonces[^1];
        var replayed = await w.SignInAsync(w.Host.Browser(), Member, new IdTokenTweak(Nonce: earlierNonce));
        Assert.Equal("/sign-in?error=oidc_nonce", replayed.Location);
        Assert.False(replayed.SetsSession);

        // The same callback (code and state) sent again, from the browser that began it with its cookie put back.
        var again = w.Host.Browser();
        var url = await w.StartAsync(again);
        var back = w.Stub.Approve(url, Member);
        var jar = again.Jar.Header; // the state cookie, before the callback deletes it
        Assert.Equal("/admin", (await World.BackAsync(again, back)).Location);
        var thief = w.Host.Browser();
        var request = new HttpRequestMessage(HttpMethod.Get, SignInBrowser.Local(back));
        request.Headers.TryAddWithoutValidation("Cookie", jar.Split("; ").First(c => c.Contains("blog_oidc")));
        var second = await thief.Http.SendAsync(request);
        Assert.Equal("/sign-in?error=state_invalid", second.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await thief.Http.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task A_code_redeemed_without_this_sign_ins_PKCE_verifier_is_refused()
    {
        await using var w = await World.StartAsync();
        await w.BindAsync(w.Owner, Member);

        // Someone holds a code issued for another sign-in (its challenge, its verifier) and brings it to their own
        // sign-in's callback: their state is genuine, but the verifier sent at the exchange isn't the code's.
        var victim = w.Host.Browser();
        var stolen = QueryHelpersParse(w.Stub.Approve(await w.StartAsync(victim), Member))["code"];
        var attacker = w.Host.Browser();
        var own = w.Stub.Approve(await w.StartAsync(attacker), new StubAccount("attacker", "A", null));
        var mixed = new Uri($"{SignInHost.PublicUrl}/api/auth/oidc/callback?code={Uri.EscapeDataString(stolen)}&state={Uri.EscapeDataString(QueryHelpersParse(own)["state"])}");
        var outcome = await World.BackAsync(attacker, mixed);
        Assert.Equal("/sign-in?error=exchange_failed", outcome.Location);
        Assert.False(outcome.SetsSession);
        // The blog sent a verifier with every exchange (the stub counts any that came without one).
        Assert.Equal(0, w.Stub.MissingVerifier);
    }

    [Fact]
    public async Task A_callback_in_a_browser_that_did_not_begin_it_is_refused_and_does_not_spend_the_sign_in()
    {
        await using var w = await World.StartAsync();
        await w.BindAsync(w.Owner, Member);
        var mine = w.Host.Browser();
        var back = w.Stub.Approve(await w.StartAsync(mine), Member);

        var other = w.Host.Browser();
        var forced = await World.BackAsync(other, back);
        Assert.Equal("/sign-in?error=state_invalid", forced.Location);
        Assert.False(forced.SetsSession);

        Assert.Equal("/admin", (await World.BackAsync(mine, back)).Location);
    }

    [Fact]
    public async Task Accounts_are_keyed_by_issuer_and_sub_never_by_email()
    {
        await using var w = await World.StartAsync();
        await w.BindAsync(w.Owner, Member);

        // The same email under another subject is someone else: nobody here.
        Assert.Equal("/sign-in?error=not_owner", (await w.SignInAsync(w.Host.Browser(), SameEmail)).Location);

        // The same subject at another issuer is someone else too.
        Assert.Equal(HttpStatusCode.OK, (await w.SetProviderAsync(w.Other.Issuer, secret: null)).Status);
        Assert.Equal("/sign-in?error=not_owner", (await w.SignInAsync(w.Host.Browser(), Member, at: w.Other)).Location);

        // Back to the first issuer, the link holds again.
        Assert.Equal(HttpStatusCode.OK, (await w.SetProviderAsync(w.Stub.Issuer, secret: null)).Status);
        Assert.Equal("/admin", (await w.SignInAsync(w.Host.Browser(), Member)).Location);
    }

    [Fact]
    public async Task The_provider_being_down_signs_nobody_out_and_says_so()
    {
        await using var w = await World.StartAsync(configure: b => b.UseSetting("Blog:Secrets:MasterKey", MasterKey));
        await w.BindAsync(w.Owner, Member);
        var member = w.Host.Browser();
        Assert.Equal("/admin", (await w.SignInAsync(member, Member)).Location);

        w.Stub.Down = true;
        // Sessions are this blog's own: both stay signed in.
        Assert.Equal(HttpStatusCode.OK, (await member.GetJsonAsync("/api/me")).Status);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.GetJsonAsync("/api/me")).Status);

        // A sign-in begun now fails at the exchange, with the reason, and clears no cookie.
        var late = await w.SignInAsync(member, Member);
        Assert.Equal("/sign-in?error=provider_unavailable", late.Location);
        Assert.False(late.SetsSession);
        Assert.Equal(HttpStatusCode.OK, late.ApiStatus);

        // Linking from Account while it's down: back to Account with the reason, still signed in.
        var bind = await w.Owner.PostJsonAsync("/api/auth/identities/oidc", null, csrf: true);
        var failed = await World.BackAsync(w.Owner, w.Stub.Approve(new Uri(bind.Json!["url"]!.GetValue<string>()), Member));
        Assert.Equal("/admin?error=provider_unavailable", failed.Location);
        Assert.Equal(HttpStatusCode.OK, failed.ApiStatus);

        // A fresh server (nothing cached) can't even begin: the start says so.
        await using var restarted = w.Host.Restart(w.Host.SetupToken);
        var start = await restarted.Browser().Http.GetAsync("/api/auth/oidc/start");
        Assert.Equal("/sign-in?error=provider_unavailable", start.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await restarted.Browser(member).GetJsonAsync("/api/me")).Status);
    }

    [Fact]
    public async Task The_setting_refuses_what_it_cannot_trust_and_is_the_owners_alone()
    {
        await using var w = await World.StartAsync(setProvider: false);
        var http = await w.SetProviderAsync("http://id.example");
        Assert.Equal(HttpStatusCode.BadRequest, http.Status);
        Assert.Contains("https", http.Json!["detail"]!.GetValue<string>());

        // A discovery document speaking for another issuer.
        w.Stub.DiscoveryIssuer = "https://elsewhere.example";
        var mismatch = await w.SetProviderAsync(w.Stub.Issuer);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.Status);
        Assert.Contains("another issuer", mismatch.Json!["detail"]!.GetValue<string>());
        w.Stub.DiscoveryIssuer = null;

        // No secret, nothing stored yet.
        Assert.Equal(HttpStatusCode.BadRequest, (await w.SetProviderAsync(w.Stub.Issuer, secret: null)).Status);
        Assert.False((await w.Owner.GetJsonAsync($"/api/blogs/{w.Blog}/sign-in/oidc")).Json!["set"]!.GetValue<bool>());
        Assert.DoesNotContain("/api/auth/oidc/start", await (await w.Host.Browser().Http.GetAsync("/sign-in")).Content.ReadAsStringAsync());
        Assert.Equal("/sign-in?error=oidc_off", (await w.Host.Browser().Http.GetAsync("/api/auth/oidc/start")).Headers.Location!.OriginalString);

        // A provider that takes the secret only in the form works too.
        w.Stub.PostSecretOnly = true;
        Assert.Equal(HttpStatusCode.OK, (await w.SetProviderAsync(w.Stub.Issuer)).Status);
        Assert.Equal("/admin?bound=oidc", (await w.BindAsync(w.Owner, Member)).Location);

        // An editor finds nothing; a write without the CSRF header is refused.
        var invited = await w.Owner.PostJsonAsync($"/api/blogs/{w.Blog}/invitations", new { role = "editor" }, csrf: true);
        var link = invited.Json!["link"]!.GetValue<string>();
        var editor = w.Host.Browser();
        var begin = await editor.PostJsonAsync("/api/auth/invite", new { token = link[(link.LastIndexOf('/') + 1)..], provider = "discord" });
        await editor.CompleteAtProviderAsync(new Uri(begin.Json!["url"]!.GetValue<string>()), new StubAccount("500000000000000001", "Ed", null));
        Assert.Equal(HttpStatusCode.NotFound, (await editor.GetJsonAsync($"/api/blogs/{w.Blog}/sign-in/oidc")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await editor.SendAsync(HttpMethod.Delete, $"/api/blogs/{w.Blog}/sign-in/oidc", csrf: true)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Owner.SendAsync(HttpMethod.Put, $"/api/blogs/{w.Blog}/sign-in/oidc", csrf: false,
            body: new { issuer = w.Stub.Issuer, client_id = "x", client_secret = "y", display_name = "Z" })).Status);

        // Removed: the button goes, the member's link stays (unusable until it's set again).
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.SendAsync(HttpMethod.Delete, $"/api/blogs/{w.Blog}/sign-in/oidc", csrf: true)).Status);
        Assert.Equal("/sign-in?error=oidc_off", (await w.Host.Browser().Http.GetAsync("/api/auth/oidc/start")).Headers.Location!.OriginalString);
        Assert.Contains((await w.Owner.GetJsonAsync("/api/me")).Json!["identities"]!.AsArray(), i => i!["provider"]!.GetValue<string>() == "oidc");
    }

    [Fact]
    public async Task On_a_hosted_instance_a_blog_cannot_add_its_own_provider()
    {
        await using var w = await World.StartAsync(setProvider: false, configure: b => b.UseSetting("Blog:Hosting:Domain", "blogs.test"));
        var r = await w.SetProviderAsync(w.Stub.Issuer);
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Contains("hosted", r.Json!["detail"]!.GetValue<string>());
    }

    private static Dictionary<string, string> QueryHelpersParse(Uri uri) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
}
