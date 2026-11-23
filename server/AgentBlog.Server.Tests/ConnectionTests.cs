using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Agent connections over MCP OAuth (docs/specs/connections.md), driven as claude.ai drives them: discovery from the
/// /mcp 401, the authorize request with PKCE, the owner approving on the consent page in their signed-in browser, the
/// code exchange, then MCP calls with the bearer. Plus the owner's level controls and revocation.
/// </summary>
public sealed partial class ConnectionTests
{
    private const string ClaudeAi = "claude-ai";
    private const string ClaudeAiCallback = "https://claude.ai/api/mcp/auth_callback";
    private static readonly string Resource = SignInHost.PublicUrl + "/mcp";
    private static readonly StubAccount Owner = new("200000000000000001", "Lenka", null);

    [Fact]
    public async Task The_mcp_401_leads_a_client_to_the_authorization_server()
    {
        await using var host = await SignInHost.StartAsync();
        var http = host.Browser().Http;

        var challenge = await http.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        var metadata = Regex.Match(challenge.Headers.WwwAuthenticate.ToString(), "resource_metadata=\"([^\"]+)\"").Groups[1].Value;
        Assert.Equal(SignInHost.PublicUrl + "/.well-known/oauth-protected-resource", metadata);

        var prm = JsonNode.Parse(await http.GetStringAsync(new Uri(metadata).PathAndQuery))!;
        Assert.Equal(Resource, prm["resource"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_owner_approves_claude_ai_and_the_connection_works_on_their_blog_at_Propose()
    {
        await using var host = await SignInHost.StartAsync();
        var owner = host.Browser();
        await owner.SetupAsync(host.SetupToken, "discord", Owner);

        var c = await ConnectAsync(owner, label: "Claude on the phone");

        var blog = await CallToolAsync(host, c.AccessToken, "get_blog");
        Assert.Equal("propose", blog["your_level"]!.GetValue<string>());
        Assert.Equal("My blog", blog["blog"]!["title"]!.GetValue<string>());
        Assert.Equal(0, blog["posts"]!["live"]!.GetValue<int>());

        var listed = Assert.Single((await owner.GetJsonAsync("/api/connections")).Json!["connections"]!.AsArray())!;
        Assert.Equal("Claude on the phone", listed["label"]!.GetValue<string>());
        Assert.Equal("propose", listed["level"]!.GetValue<string>());
        Assert.Equal(ClaudeAi, listed["client_id"]!.GetValue<string>());
        Assert.Contains("connection_created", await host.EventKindsAsync());
    }

    [Fact]
    public async Task Without_a_session_the_consent_page_sends_the_owner_to_sign_in_and_back()
    {
        await using var host = await SignInHost.StartAsync();
        await host.Browser().SetupAsync(host.SetupToken, "discord", Owner);
        var stranger = host.Browser();

        var page = await stranger.Http.GetAsync(AuthorizeUrl(Pkce.New().Challenge, "st-1"));
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        var signIn = page.Headers.Location!.OriginalString;
        Assert.True(signIn.StartsWith("/sign-in?returnTo=%2Foauth%2Fauthorize", StringComparison.Ordinal), signIn);

        var html = await stranger.Http.GetStringAsync(signIn);
        Assert.Contains("/api/auth/discord/start?returnTo=%2Foauth%2Fauthorize", WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task A_forged_form_cannot_attach_a_connection_to_a_blog_the_account_is_not_on()
    {
        await using var host = await SignInHost.StartAsync();
        var owner = host.Browser();
        await owner.SetupAsync(host.SetupToken, "discord", Owner);

        // Someone else's blog on the same instance: it exists, it just isn't hers.
        var theirs = Guid.CreateVersion7();
        await using (var conn = new NpgsqlConnection(host.App.AppConnectionString))
        {
            var them = Guid.CreateVersion7();
            await conn.ExecuteAsync("""
                INSERT INTO account (id, display_name, created_at) VALUES (@them, 'Someone else', now());
                INSERT INTO blog (id, slug, title, created_at) VALUES (@theirs, 'theirs', 'Their blog', now());
                INSERT INTO blog_member (blog_id, account_id, created_at) VALUES (@theirs, @them, now());
                """, new { them, theirs });
        }

        var page = await ConsentPageAsync(owner, Pkce.New(), "st-2");
        Assert.DoesNotContain(theirs.ToString(), page); // not even offered
        var refused = await PostConsentAsync(owner, page, new() { ["blog"] = theirs.ToString(), ["label"] = "forged" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode); // the page again with the reason, never a redirect with a code
        Assert.Contains("You can only connect a blog you're on.", WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync()));
        Assert.Empty((await owner.GetJsonAsync("/api/connections")).Json!["connections"]!.AsArray());
        await using var check = new NpgsqlConnection(host.App.AppConnectionString);
        Assert.Equal(0, await check.ExecuteScalarAsync<long>("SELECT count(*) FROM connection WHERE blog_id = @theirs", new { theirs }));
    }

    [Fact]
    public async Task Full_needs_the_owners_confirmation_and_a_level_change_applies_to_the_next_call()
    {
        await using var host = await SignInHost.StartAsync();
        var owner = host.Browser();
        await owner.SetupAsync(host.SetupToken, "discord", Owner);
        var c = await ConnectAsync(owner);

        var unconfirmed = await owner.SendAsync(HttpMethod.Patch, $"/api/connections/{c.Id}", csrf: true, body: new { level = "full" });
        Assert.Equal(HttpStatusCode.Conflict, unconfirmed.Status);
        Assert.Equal("confirm_full", unconfirmed.Json!["code"]!.GetValue<string>());
        Assert.Equal("propose", (await CallToolAsync(host, c.AccessToken, "get_blog"))["your_level"]!.GetValue<string>());

        var invalid = await owner.SendAsync(HttpMethod.Patch, $"/api/connections/{c.Id}", csrf: true, body: new { level = "admin" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);

        var full = await owner.SendAsync(HttpMethod.Patch, $"/api/connections/{c.Id}", csrf: true, body: new { level = "full", confirm_full = true });
        Assert.Equal(HttpStatusCode.NoContent, full.Status);
        Assert.Equal("full", (await CallToolAsync(host, c.AccessToken, "get_blog"))["your_level"]!.GetValue<string>());

        await owner.SendAsync(HttpMethod.Patch, $"/api/connections/{c.Id}", csrf: true, body: new { level = "publish" });
        Assert.Equal("publish", (await CallToolAsync(host, c.AccessToken, "get_blog"))["your_level"]!.GetValue<string>());

        Assert.Equal(2, (await host.EventKindsAsync()).Count(k => k == "connection_level_changed"));
    }

    [Fact]
    public async Task An_agents_bearer_never_reaches_the_owners_API()
    {
        await using var host = await SignInHost.StartAsync();
        var owner = host.Browser();
        await owner.SetupAsync(host.SetupToken, "discord", Owner);
        var c = await ConnectAsync(owner);

        var agent = host.Browser().Http; // no cookies: what an agent has is its bearer
        using var list = new HttpRequestMessage(HttpMethod.Get, "/api/connections");
        list.Headers.Authorization = new("Bearer", c.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.SendAsync(list)).StatusCode);

        using var raise = new HttpRequestMessage(HttpMethod.Patch, $"/api/connections/{c.Id}")
        {
            Content = new StringContent("""{"level":"full","confirm_full":true}""", Encoding.UTF8, "application/json"),
        };
        raise.Headers.Authorization = new("Bearer", c.AccessToken);
        raise.Headers.Add("X-Blog-Request", "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.SendAsync(raise)).StatusCode);
        Assert.Equal("propose", (await CallToolAsync(host, c.AccessToken, "get_blog"))["your_level"]!.GetValue<string>());

        // And no MCP tool changes a level or a connection.
        var tools = await ListToolsAsync(host, c.AccessToken);
        Assert.DoesNotContain(tools, t => t.Contains("level") || t.Contains("connection"));
    }

    [Fact]
    public async Task Revoking_refuses_the_next_call_and_the_refresh_token_too()
    {
        await using var host = await SignInHost.StartAsync();
        var owner = host.Browser();
        await owner.SetupAsync(host.SetupToken, "discord", Owner);
        var c = await ConnectAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await McpAsync(host, c.AccessToken, "tools/list")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Delete, $"/api/connections/{c.Id}", csrf: true)).Status);

        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(host, c.AccessToken, "tools/list")).StatusCode);
        var refresh = await host.Browser().Http.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = c.RefreshToken, ["client_id"] = ClaudeAi, ["resource"] = Resource,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
        Assert.Empty((await owner.GetJsonAsync("/api/connections")).Json!["connections"]!.AsArray());
        Assert.Contains("connection_revoked", await host.EventKindsAsync());
    }

    [Fact]
    public async Task Another_account_cannot_see_change_or_revoke_the_owners_connection()
    {
        await using var host = await SignInHost.StartAsync();
        var owner = host.Browser();
        await owner.SetupAsync(host.SetupToken, "discord", Owner);
        var c = await ConnectAsync(owner);

        // A second account on the instance (a hosted one has many), owning a blog of its own, with no share in this one.
        var other = new StubAccount("200000000000000099", "Someone else", null);
        await using (var conn = new NpgsqlConnection(host.App.AppConnectionString))
        {
            var id = Guid.CreateVersion7();
            await conn.ExecuteAsync("""
                INSERT INTO account (id, display_name, created_at) VALUES (@id, 'Someone else', now());
                INSERT INTO account_identity (provider, provider_account_id, account_id, created_at) VALUES ('discord', @pid, @id, now());
                INSERT INTO blog (id, slug, title, created_at) VALUES (@blog, 'other-blog', 'Theirs', now());
                INSERT INTO blog_member (blog_id, account_id, role, created_at) VALUES (@blog, @id, 'owner', now());
                """, new { id, pid = other.Id, blog = Guid.CreateVersion7() });
        }
        var them = host.Browser();
        Assert.Equal(HttpStatusCode.OK, (await them.SignInAsync("discord", other)).ApiStatus);

        Assert.Empty((await them.GetJsonAsync("/api/connections")).Json!["connections"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound,
            (await them.SendAsync(HttpMethod.Patch, $"/api/connections/{c.Id}", csrf: true, body: new { level = "full", confirm_full = true })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await them.SendAsync(HttpMethod.Delete, $"/api/connections/{c.Id}", csrf: true)).Status);
        Assert.Equal("propose", (await CallToolAsync(host, c.AccessToken, "get_blog"))["your_level"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------------------------------------ helpers

    internal sealed record Connected(Guid Id, string AccessToken, string RefreshToken);

    internal sealed record Pkce(string Verifier, string Challenge)
    {
        public static Pkce New()
        {
            var verifier = StubProviders.Base64Url(RandomNumberGenerator.GetBytes(32));
            return new Pkce(verifier, StubProviders.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        }
    }

    /// <summary>The whole flow as claude.ai runs it, the owner approving in <paramref name="owner"/>'s browser.</summary>
    internal static async Task<Connected> ConnectAsync(SignInBrowser owner, string? label = null)
    {
        var pkce = Pkce.New();
        label ??= "test-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var page = await ConsentPageAsync(owner, pkce, "st-c");
        var blog = Regex.Match(page, "name=\"blog\" value=\"([^\"]+)\" checked").Groups[1].Value;
        var allowed = await PostConsentAsync(owner, page, new() { ["blog"] = blog, ["label"] = label });
        Assert.True(allowed.StatusCode == HttpStatusCode.Redirect, $"{allowed.StatusCode} {await allowed.Content.ReadAsStringAsync()}");
        var location = allowed.Headers.Location!;
        Assert.StartsWith(ClaudeAiCallback, location.AbsoluteUri);
        var code = QueryHelpers.ParseQuery(location.Query)["code"].ToString();

        var r = await owner.Http.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = ClaudeAiCallback,
            ["client_id"] = ClaudeAi, ["code_verifier"] = pkce.Verifier, ["resource"] = Resource,
        }));
        var tokens = JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();
        Assert.True(r.IsSuccessStatusCode, tokens.ToJsonString());

        var listed = (await owner.GetJsonAsync("/api/connections")).Json!["connections"]!.AsArray()
            .Single(x => x!["label"]!.GetValue<string>() == label)!;
        return new Connected(Guid.Parse(listed["id"]!.GetValue<string>()),
            tokens["access_token"]!.GetValue<string>(), tokens["refresh_token"]!.GetValue<string>());
    }

    internal static async Task<string> ConsentPageAsync(SignInBrowser owner, Pkce pkce, string state)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AuthorizeUrl(pkce.Challenge, state));
        request.Headers.Accept.ParseAdd("text/html");
        var page = await owner.Http.SendAsync(request);
        var html = await page.Content.ReadAsStringAsync();
        Assert.True(page.StatusCode == HttpStatusCode.OK, $"{page.StatusCode} {page.Headers.Location} {html}");
        return html;
    }

    internal static async Task<HttpResponseMessage> PostConsentAsync(SignInBrowser owner, string pageHtml, Dictionary<string, string> choices)
    {
        var hidden = HiddenInput().Matches(pageHtml).Select(m => new KeyValuePair<string, string>(
            WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)));
        var fields = hidden.Where(kv => !choices.ContainsKey(kv.Key) && kv.Key != "consent")
            .Concat(choices).Append(new("consent", "allow")).ToList();
        return await owner.Http.PostAsync("/oauth/authorize", new FormUrlEncodedContent(fields));
    }

    [GeneratedRegex("<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\">")]
    private static partial Regex HiddenInput();

    internal static string AuthorizeUrl(string challenge, string state) =>
        QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = ClaudeAi, ["redirect_uri"] = ClaudeAiCallback, ["scope"] = "blog",
            ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["resource"] = Resource,
        });

    /// <summary>One JSON-RPC request to /mcp with the bearer, as a client sends it (stateless: no session to open first).</summary>
    internal static async Task<HttpResponseMessage> McpAsync(SignInHost host, string bearer, string method, object? @params = null)
    {
        var http = host.Browser().Http;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = @params ?? new { } }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", bearer);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        return await http.SendAsync(request);
    }

    /// <summary>The JSON-RPC result, whether the server answered with JSON or with one SSE event.</summary>
    internal static async Task<JsonObject> RpcResultAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode} {text}");
        var json = text.TrimStart().StartsWith('{')
            ? text
            : string.Join("", text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()));
        var message = JsonNode.Parse(json)!.AsObject();
        Assert.True(message["error"] is null, message.ToJsonString());
        return message["result"]!.AsObject();
    }

    internal static async Task<JsonObject> CallToolAsync(SignInHost host, string bearer, string tool, object? arguments = null)
    {
        var result = await RpcResultAsync(await McpAsync(host, bearer, "tools/call", new { name = tool, arguments = arguments ?? new { } }));
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.True(result["isError"]?.GetValue<bool>() != true, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    internal static async Task<List<string>> ListToolsAsync(SignInHost host, string bearer) =>
        [.. (await RpcResultAsync(await McpAsync(host, bearer, "tools/list")))["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())];
}
