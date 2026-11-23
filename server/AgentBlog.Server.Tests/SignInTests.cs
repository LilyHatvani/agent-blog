using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentKit.ProviderSignIn;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Owner sign-in and setup (docs/specs/auth.md) through the real host, with stub providers that behave like Google and
/// Discord: a browser with a cookie jar on the public origin, the provider round trip, the session cookie, and /api
/// behind it. Setup binds one owner for good, so every test starts its own host on its own database.
/// </summary>
public sealed class SignInTests
{
    private static readonly StubAccount Owner = new("200000000000000001", "Lenka", null);
    private static readonly StubAccount OwnerGoogle = new("110000000000000000001", "Lenka", "owner@example.com");
    private static readonly StubAccount Stranger = new("200000000000000002", "Stranger", null);

    // --------------------------------------------------------------------------------------------- setup

    [Fact]
    public async Task Setup_claims_the_blog_creates_the_owner_and_signs_them_in_by_cookie_alone()
    {
        await using var host = await SignInHost.StartAsync();
        var browser = host.Browser();

        var landed = await browser.SetupAsync(host.SetupToken, "discord", Owner);
        Assert.Equal("/admin", landed.Location);
        Assert.True(landed.SetsSession);

        var me = await browser.GetJsonAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, me.Status);
        Assert.Equal("Lenka", me.Json!["account"]!["display_name"]!.GetValue<string>());
        var blog = Assert.Single(me.Json["blogs"]!.AsArray())!;
        Assert.Equal("owner", blog["role"]!.GetValue<string>());
        Assert.Equal("test-blog", blog["slug"]!.GetValue<string>());
        var way = Assert.Single(me.Json["identities"]!.AsArray())!;
        Assert.Equal("discord", way["provider"]!.GetValue<string>());
        Assert.True(way["this_session"]!.GetValue<bool>());

        // What happened is on the record.
        var kinds = await host.EventKindsAsync();
        Assert.Equal(["account_created", "blog_created", "identity_bound", "setup_completed"], kinds);
    }

    [Fact]
    public async Task The_session_cookie_is_HttpOnly_Secure_Lax_and_lasts_thirty_days()
    {
        await using var host = await SignInHost.StartAsync();
        var landed = await host.Browser().SetupAsync(host.SetupToken, "discord", Owner);

        var cookie = landed.SessionCookieHeader!;
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"max-age={(int)TimeSpan.FromDays(30).TotalSeconds}", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Setup_burns_the_token_and_a_second_setup_is_refused_even_with_another_account()
    {
        await using var host = await SignInHost.StartAsync();
        await host.Browser().SetupAsync(host.SetupToken, "discord", Owner);

        var again = await host.Browser().PostJsonAsync("/api/auth/setup", new { token = host.SetupToken, provider = "discord" });
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Equal("setup_done", again.Json!["code"]!.GetValue<string>());

        // A stranger can't sign in either: setup made one owner.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Browser().SignInAsync("discord", Stranger)).ApiStatus);
    }

    [Fact]
    public async Task A_setup_begun_before_another_finished_binds_nothing()
    {
        await using var host = await SignInHost.StartAsync();
        var early = host.Browser();
        var url = await early.BeginSetupAsync(host.SetupToken, "discord");
        await host.Browser().SetupAsync(host.SetupToken, "google", OwnerGoogle);

        var late = await early.CompleteAtProviderAsync(url, Stranger);
        Assert.Equal("/setup?error=setup_closed", late.Location);
        Assert.False(late.SetsSession);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Browser().SignInAsync("discord", Stranger)).ApiStatus);
    }

    [Fact]
    public async Task Setup_refuses_a_wrong_token_an_unoffered_provider_and_a_form_post()
    {
        await using var host = await SignInHost.StartAsync(google: false);
        var browser = host.Browser();

        var wrong = await browser.PostJsonAsync("/api/auth/setup", new { token = "x" + host.SetupToken, provider = "discord" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        var google = await browser.PostJsonAsync("/api/auth/setup", new { token = host.SetupToken, provider = "google" });
        Assert.Equal(HttpStatusCode.BadRequest, google.Status);
        var form = await browser.Http.PostAsync("/api/auth/setup", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["token"] = host.SetupToken, ["provider"] = "discord" }));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, form.StatusCode);

        // None of that used setup up.
        Assert.Equal("/admin", (await browser.SetupAsync(host.SetupToken, "discord", Owner)).Location);
    }

    [Fact]
    public async Task Without_a_setup_token_on_the_server_setup_is_closed()
    {
        await using var host = await SignInHost.StartAsync(setupToken: "");
        var r = await host.Browser().PostJsonAsync("/api/auth/setup", new { token = new string('a', 40), provider = "discord" });
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Equal("setup_closed", r.Json!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_new_setup_token_reopens_setup_for_the_same_owner_and_ends_their_old_sessions()
    {
        await using var host = await SignInHost.StartAsync();
        var old = host.Browser();
        await old.SetupAsync(host.SetupToken, "discord", Owner);
        var blogBefore = (await old.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();

        var newToken = "setup-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        await using var restarted = host.Restart(newToken);
        var recovered = restarted.Browser();
        Assert.Equal("/admin", (await recovered.SetupAsync(newToken, "google", OwnerGoogle)).Location);

        var me = (await recovered.GetJsonAsync("/api/me")).Json!;
        Assert.Equal(blogBefore, Assert.Single(me["blogs"]!.AsArray())!["id"]!.GetValue<string>());
        Assert.Equal(2, me["identities"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.Unauthorized, (await restarted.Browser(old).GetJsonAsync("/api/me")).Status);
    }

    // ------------------------------------------------------------------------------------------- sign-in

    [Fact]
    public async Task Only_the_bound_account_signs_in_and_a_stranger_gets_no_session()
    {
        await using var host = await SignInHost.StartAsync();
        await host.Browser().SetupAsync(host.SetupToken, "discord", Owner);

        var owner = await host.Browser().SignInAsync("discord", Owner);
        Assert.Equal("/admin", owner.Location);
        Assert.Equal(HttpStatusCode.OK, owner.ApiStatus);

        var stranger = await host.Browser().SignInAsync("discord", Stranger);
        Assert.Equal("/sign-in?error=not_owner", stranger.Location);
        Assert.False(stranger.SetsSession);
        Assert.Equal(HttpStatusCode.Unauthorized, stranger.ApiStatus);
    }

    [Fact]
    public async Task A_cancelled_sign_in_comes_back_with_its_reason_and_no_session()
    {
        await using var host = await SignInHost.StartAsync();
        var browser = host.Browser();
        var start = await browser.Http.GetAsync("/api/auth/discord/start");

        var back = await browser.Http.GetAsync(SignInBrowser.Local(StubProviders.Deny(start.Headers.Location!)));
        Assert.Equal("/sign-in?error=denied", back.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetJsonAsync("/api/me")).Status);
    }

    // --------------------------------------------------------------------------------- ways in, sign-out

    [Fact]
    public async Task A_signed_in_owner_adds_a_second_way_in_and_can_sign_in_with_it()
    {
        await using var host = await SignInHost.StartAsync();
        var browser = host.Browser();
        await browser.SetupAsync(host.SetupToken, "discord", Owner);

        var begin = await browser.PostJsonAsync("/api/auth/identities/google", null, csrf: true);
        Assert.Equal(HttpStatusCode.OK, begin.Status);
        var bound = await browser.CompleteAtProviderAsync(new Uri(begin.Json!["url"]!.GetValue<string>()), OwnerGoogle);
        Assert.Equal("/admin?bound=google", bound.Location);

        Assert.Equal(HttpStatusCode.OK, (await host.Browser().SignInAsync("google", OwnerGoogle)).ApiStatus);
    }

    [Fact]
    public async Task The_last_way_in_cannot_be_removed_and_removing_one_ends_the_sessions_it_began()
    {
        await using var host = await SignInHost.StartAsync();
        var viaDiscord = host.Browser();
        await viaDiscord.SetupAsync(host.SetupToken, "discord", Owner);

        var last = await viaDiscord.SendAsync(HttpMethod.Delete, $"/api/auth/identities/discord/{Owner.Id}", csrf: true);
        Assert.Equal(HttpStatusCode.Conflict, last.Status);
        Assert.Equal("last_identity", last.Json!["code"]!.GetValue<string>());

        var begin = await viaDiscord.PostJsonAsync("/api/auth/identities/google", null, csrf: true);
        await viaDiscord.CompleteAtProviderAsync(new Uri(begin.Json!["url"]!.GetValue<string>()), OwnerGoogle);
        var viaGoogle = host.Browser();
        await viaGoogle.SignInAsync("google", OwnerGoogle);

        var removed = await viaGoogle.SendAsync(HttpMethod.Delete, $"/api/auth/identities/discord/{Owner.Id}", csrf: true);
        Assert.Equal(HttpStatusCode.NoContent, removed.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await viaDiscord.GetJsonAsync("/api/me")).Status);
        Assert.Equal(HttpStatusCode.OK, (await viaGoogle.GetJsonAsync("/api/me")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Browser().SignInAsync("discord", Owner)).ApiStatus);
    }

    [Fact]
    public async Task Sign_out_ends_this_browser_and_sign_out_everywhere_ends_every_browser()
    {
        await using var host = await SignInHost.StartAsync();
        var a = host.Browser();
        await a.SetupAsync(host.SetupToken, "discord", Owner);
        var b = host.Browser();
        await b.SignInAsync("discord", Owner);
        var c = host.Browser();
        await c.SignInAsync("discord", Owner);

        Assert.Equal(HttpStatusCode.NoContent, (await a.PostJsonAsync("/api/auth/sign-out", null, csrf: true)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetJsonAsync("/api/me")).Status);
        Assert.Equal(HttpStatusCode.OK, (await b.GetJsonAsync("/api/me")).Status);

        var everywhere = await b.PostJsonAsync("/api/auth/sign-out-everywhere", null, csrf: true);
        Assert.Equal(2, everywhere.Json!["ended"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetJsonAsync("/api/me")).Status);
    }

    [Fact]
    public async Task A_write_without_the_CSRF_header_is_refused_and_changes_nothing()
    {
        await using var host = await SignInHost.StartAsync();
        var browser = host.Browser();
        await browser.SetupAsync(host.SetupToken, "discord", Owner);

        var forged = await browser.PostJsonAsync("/api/auth/sign-out", null, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, forged.Status);
        Assert.Equal("csrf", forged.Json!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetJsonAsync("/api/me")).Status);
    }

    [Fact]
    public async Task The_sign_in_endpoints_are_rate_limited_per_client()
    {
        await using var host = await SignInHost.StartAsync(rateLimit: 3);
        var browser = host.Browser();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Redirect, (await browser.Http.GetAsync("/api/auth/discord/start")).StatusCode);

        var refused = await browser.Http.GetAsync("/api/auth/discord/start");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("60", refused.Headers.RetryAfter!.ToString());
    }

    [Fact]
    public async Task On_a_fresh_instance_sign_in_points_to_setup_instead_of_a_dead_end()
    {
        await using var host = await SignInHost.StartAsync();
        var page = await (await host.Browser().Http.GetAsync("/sign-in")).Content.ReadAsStringAsync();
        Assert.Contains("This blog has no owner yet.", page);
        Assert.Contains("href=\"/setup\"", page);

        await host.Browser().SetupAsync(host.SetupToken, "discord", Owner);
        var after = await (await host.Browser().Http.GetAsync("/sign-in")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("no owner yet", after);
        Assert.Contains("Sign in with Discord", after);
    }

    [Fact]
    public async Task The_owner_pages_answer_and_admin_sends_a_stranger_to_sign_in()
    {
        await using var host = await SignInHost.StartAsync();
        var browser = host.Browser();

        var setup = await browser.Http.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.Contains("script-src 'self'", setup.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("Continue with Discord", await setup.Content.ReadAsStringAsync());

        var admin = await browser.Http.GetAsync("/admin");
        Assert.Equal("/sign-in?returnTo=%2Fadmin", admin.Headers.Location!.OriginalString);

        await browser.SetupAsync(host.SetupToken, "discord", Owner);
        var mine = await browser.Http.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Contains("<div id=\"app\">", await mine.Content.ReadAsStringAsync());
        Assert.Contains("script-src 'self'", mine.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("/sign-in?returnTo=%2Fadmin", (await host.Browser().Http.GetAsync("/admin/posts")).Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.Http.GetAsync("/admin/missing.js")).StatusCode);

        // An error code in the URL is shown as its sentence, never echoed.
        var odd = await (await browser.Http.GetAsync("/sign-in?error=%3Cscript%3E")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>", odd);
        Assert.Contains("Something went wrong", odd);
    }
}

/// <summary>A host with stub providers, its own database, and a setup token.</summary>
internal sealed class SignInHost : IAsyncDisposable
{
    public const string PublicUrl = "https://blog.test";

    public BlogApp App { get; }
    public StubProviders Providers { get; }
    public string SetupToken { get; }
    private readonly WebApplicationFactory<Program> _factory;
    private readonly bool _owned;

    private SignInHost(BlogApp app, StubProviders providers, string setupToken, WebApplicationFactory<Program> factory, bool owned)
    {
        (App, Providers, SetupToken, _factory, _owned) = (app, providers, setupToken, factory, owned);
    }

    public static async Task<SignInHost> StartAsync(string? setupToken = null, bool google = true, int rateLimit = 100_000,
        Action<IWebHostBuilder>? configure = null)
    {
        setupToken ??= "setup-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var providers = new StubProviders();
        var app = new BlogApp(b =>
        {
            Configure(b, providers, setupToken, google, rateLimit);
            configure?.Invoke(b);
        });
        await app.InitializeAsync();
        return new SignInHost(app, providers, setupToken, app.Factory, owned: true);
    }

    private static void Configure(IWebHostBuilder b, StubProviders providers, string setupToken, bool google, int rateLimit)
    {
        b.UseSetting("Blog:PublicUrl", PublicUrl);
        b.UseSetting("Blog:Slug", "test-blog");
        b.UseSetting("Blog:SetupToken", setupToken);
        b.UseSetting("Blog:Auth:RateLimitPerMinute", rateLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (google)
        {
            b.UseSetting("Blog:Auth:Google:ClientId", StubProviders.GoogleClientId);
            b.UseSetting("Blog:Auth:Google:ClientSecret", StubProviders.GoogleClientSecret);
            b.UseSetting("Blog:Auth:Google:AuthorizeEndpoint", StubProviders.GoogleAuthorize);
            b.UseSetting("Blog:Auth:Google:TokenEndpoint", StubProviders.GoogleToken);
            b.UseSetting("Blog:Auth:Google:JwksUri", StubProviders.GoogleJwks);
        }
        b.UseSetting("Blog:Auth:Discord:ClientId", StubProviders.DiscordClientId);
        b.UseSetting("Blog:Auth:Discord:ClientSecret", StubProviders.DiscordClientSecret);
        b.UseSetting("Blog:Auth:Discord:AuthorizeEndpoint", StubProviders.DiscordAuthorize);
        b.UseSetting("Blog:Auth:Discord:TokenEndpoint", StubProviders.DiscordToken);
        b.UseSetting("Blog:Auth:Discord:UserEndpoint", StubProviders.DiscordUser);
        b.ConfigureTestServices(s => s.AddHttpClient(ProviderSignInDefaults.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubTransport(providers)));
    }

    /// <summary>The same database and providers, restarted with another setup token.</summary>
    public SignInHost Restart(string setupToken, Action<IWebHostBuilder>? configure = null)
    {
        var factory = _factory.WithWebHostBuilder(b => { b.UseSetting("Blog:SetupToken", setupToken); configure?.Invoke(b); });
        _ = factory.Server;
        return new SignInHost(App, Providers, setupToken, factory, owned: false);
    }

    public SignInBrowser Browser() => Browser(new CookieJar());

    /// <summary>The same browser (its cookies) against this host.</summary>
    public SignInBrowser Browser(SignInBrowser same) => Browser(same.Jar);

    private SignInBrowser Browser(CookieJar jar) => new(_factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(PublicUrl),
        AllowAutoRedirect = false,
        HandleCookies = false,
    }), Providers, jar);

    public async Task<List<string>> EventKindsAsync()
    {
        await using var conn = new NpgsqlConnection(App.AppConnectionString);
        return [.. await conn.QueryAsync<string>("SELECT kind FROM event ORDER BY id")];
    }

    public async ValueTask DisposeAsync()
    {
        if (_owned)
        {
            await App.DisposeAsync();
            Providers.Dispose();
        }
        else
        {
            await _factory.DisposeAsync();
        }
    }
}

/// <summary>A browser's cookies on one origin: set from Set-Cookie, removed by an expired one.</summary>
internal sealed class CookieJar
{
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public void Take(HttpResponseMessage r)
    {
        if (!r.Headers.TryGetValues("Set-Cookie", out var set)) return;
        foreach (var header in set)
        {
            var pair = header.Split(';')[0];
            var eq = pair.IndexOf('=');
            var (name, value) = (pair[..eq], pair[(eq + 1)..]);
            var expired = Regex.IsMatch(header, @"max-age=0\b|expires=Thu, 01 Jan 1970", RegexOptions.IgnoreCase);
            if (expired || value.Length == 0) _cookies.Remove(name);
            else _cookies[name] = value;
        }
    }

    public string Header => string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}"));
}

internal sealed record Outcome(string Location, bool SetsSession, string? SessionCookieHeader, HttpStatusCode ApiStatus);

internal sealed record JsonResult(HttpStatusCode Status, JsonObject? Json);

internal sealed class SignInBrowser(HttpClient http, StubProviders providers, CookieJar jar)
{
    public HttpClient Http { get; } = new(new JarHandler(http, jar)) { BaseAddress = http.BaseAddress };
    public CookieJar Jar => jar;

    public async Task<Outcome> SetupAsync(string token, string provider, StubAccount account) =>
        await CompleteAtProviderAsync(await BeginSetupAsync(token, provider), account);

    public async Task<Uri> BeginSetupAsync(string token, string provider)
    {
        var r = await PostJsonAsync("/api/auth/setup", new { token, provider });
        Assert.True(r.Status == HttpStatusCode.OK, r.Json?.ToJsonString());
        return new Uri(r.Json!["url"]!.GetValue<string>());
    }

    public async Task<Outcome> SignInAsync(string provider, StubAccount account)
    {
        var start = await Http.GetAsync($"/api/auth/{provider}/start");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        return await CompleteAtProviderAsync(start.Headers.Location!, account);
    }

    /// <summary>At the provider as <paramref name="account"/>, then back to the callback; then probes /api/me.</summary>
    public async Task<Outcome> CompleteAtProviderAsync(Uri authorize, StubAccount account)
    {
        var back = await Http.GetAsync(Local(providers.Approve(authorize, account)));
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        var session = back.Headers.TryGetValues("Set-Cookie", out var set)
            ? set.FirstOrDefault(c => c.StartsWith("blog_session=", StringComparison.Ordinal) && !c.StartsWith("blog_session=;", StringComparison.Ordinal))
            : null;
        var api = (await Http.GetAsync("/api/me")).StatusCode;
        return new Outcome(back.Headers.Location!.OriginalString, session is not null, session, api);
    }

    public static string Local(Uri absolute) => absolute.PathAndQuery;

    public async Task<JsonResult> GetJsonAsync(string path) => await Read(await Http.GetAsync(path));

    public Task<JsonResult> PostJsonAsync(string path, object? body, bool csrf = false) =>
        SendAsync(HttpMethod.Post, path, csrf, body);

    public async Task<JsonResult> SendAsync(HttpMethod method, string path, bool csrf = false, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        if (csrf) request.Headers.Add("X-Blog-Request", "1");
        return await Read(await Http.SendAsync(request));
    }

    private static async Task<JsonResult> Read(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return new JsonResult(r.StatusCode, text.Length > 0 && text.TrimStart().StartsWith('{') ? JsonNode.Parse(text)!.AsObject() : null);
    }

    private sealed class JarHandler(HttpClient inner, CookieJar jar) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var forward = new HttpRequestMessage(request.Method, request.RequestUri!.PathAndQuery) { Content = request.Content };
            foreach (var h in request.Headers) forward.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (jar.Header.Length > 0) forward.Headers.TryAddWithoutValidation("Cookie", jar.Header);
            var response = await inner.SendAsync(forward, ct);
            jar.Take(response);
            return response;
        }
    }
}
