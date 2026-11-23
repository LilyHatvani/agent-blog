using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using AgentBlog.Auth;
using AgentBlog.Database;
using AgentKit.McpOAuth;

namespace AgentBlog.Connections;

/// <summary>
/// The OAuth server hosted connectors (claude.ai, ChatGPT, Claude Code) sign in with, from <c>AgentKit.McpOAuth</c>
/// (decision 0007): PKCE S256, hashed reference tokens, audience pinned to <c>&lt;PublicUrl&gt;/mcp</c>, per-call
/// revocation. One issuer for the whole instance; the consent page asks which blog the connection is for (decision 0005).
/// On only when <c>Blog__PublicUrl</c> is set.
/// </summary>
public static class BlogOAuth
{
    /// <summary>The one scope. Every grant names it, so it never changes.</summary>
    public const string Scope = "blog";

    public const string ConnectionIdClaim = "agentblog:connection_id";
    public const string BlogIdClaim = "agentblog:blog_id";
    public const string LevelClaim = "agentblog:level";
    public const string PersonIdClaim = "agentblog:person_id";
    public const string RoleClaim = "agentblog:role";

    public static IServiceCollection AddBlogOAuth(this IServiceCollection services, IConfiguration config, ConnectionSettings connections)
    {
        var perClient = config.GetValue("Blog:OAuth:AuthorizeRateLimitPerMinute", 30);
        var overall = config.GetValue("Blog:OAuth:AuthorizeGlobalRateLimitPerMinute", Math.Max(300, perClient));
        var hops = config.GetValue("Blog:Proxy:ForwardedForHops", 0);
        services.AddMcpOAuth(o =>
        {
            o.PublicUrl = config["Blog:PublicUrl"] is { Length: > 0 } url ? url : null;
            o.ConnectionString = connections.App;
            o.Scope = Scope;
            o.ResourceName = config["Blog:Title"] is { Length: > 0 } title ? title : "Agent Blog";
            o.ConsentDescription = "It will work on the blog you pick at Propose: it writes drafts and suggests, and publishing waits for you. You can raise or lower that, or revoke it, any time from your blog's admin page.";
            o.Clients.Add(McpOAuthClient.ClaudeAi);
            o.Clients.Add(McpOAuthClient.ClaudeCode);
            o.MetadataDocumentHosts.Add("claude.ai");
            o.MetadataDocumentHosts.Add("chatgpt.com");
            o.AuthorizeRateLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                    RateLimitPartition.GetFixedWindowLimiter("client:" + SignIn.ClientAddress(ctx, hops), _ => Window(perClient))),
                PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                    RateLimitPartition.GetFixedWindowLimiter("all", _ => Window(overall))));
        });
        services.AddHttpContextAccessor();
        services.AddSingleton<ConnectionStore>();
        services.AddSingleton<IMcpConnectionStore, BlogConnectionStore>();
        services.AddSingleton<IMcpConsentOwner, BlogConsentOwner>();
        return services;
    }

    private static FixedWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };

    /// <summary>The caller of an MCP request, from the principal the gate set (<see cref="BlogConnectionStore.ResolveAsync"/>).</summary>
    public static ConnectionStore.Caller? Caller(ClaimsPrincipal? user) =>
        user?.FindFirst(ConnectionIdClaim)?.Value is { } c && Guid.TryParse(c, out var connectionId)
        && user.FindFirst(BlogIdClaim)?.Value is { } b && Guid.TryParse(b, out var blogId)
        && user.FindFirst(LevelClaim)?.Value is { } level
        && user.FindFirst(PersonIdClaim)?.Value is { } p && Guid.TryParse(p, out var personId)
        && user.FindFirst(RoleClaim)?.Value is { } role
            ? new ConnectionStore.Caller(connectionId, blogId, level, personId, role)
            : null;
}

/// <summary>
/// The blog's connections behind OAuth grants (<see cref="IMcpConnectionStore"/>): the consent page's choices (which
/// blog, a label), creating the connection at Propose, and resolving it on every <c>/mcp</c> call.
/// </summary>
public sealed class BlogConnectionStore(ConnectionStore connections, Sessions sessions, IHttpContextAccessor http) : IMcpConnectionStore
{
    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string DefaultLabel(McpConsentRequest request) => $"{request.ClientName} · {request.Now:yyyy-MM-dd}";

    public async ValueTask<string> RenderChoicesAsync(McpConsentRequest request, IFormCollection? form, CancellationToken ct)
    {
        var ctx = http.HttpContext!;
        var session = await sessions.AuthenticateAsync(ctx, ct);
        var blogs = session is null ? [] : await connections.MemberBlogsAsync(session.AccountId, ct);
        if (blogs.Count == 0) return "<p>You aren't on a blog here, so there's nothing to connect it to.</p>";

        var picked = form?["blog"].ToString();
        var options = string.Concat(blogs.Select((b, i) =>
        {
            var on = picked is { Length: > 0 } ? picked == b.Id.ToString() : i == 0;
            return $"<label class=\"check\"><input type=\"radio\" name=\"blog\" value=\"{b.Id}\"{(on ? " checked" : "")}> {H(b.Title)}</label>";
        }));
        // On a blog someone else owns, it's this person's agent: held to what they may do, its level the owner's to set.
        var others = blogs.Where(b => b.Role != Roles.Owner)
            .Select(b => $"{b.Title}: you're {(b.Role == Roles.Editor ? "an editor, so at most Publish" : "a writer, so it stays at Propose")}").ToList();
        var notOwner = others.Count == 0 ? "" :
            "<p class=\"muted\">On a blog you write for, it is <strong>your</strong> agent: it never does more than you may ("
            + H(string.Join("; ", others)) + "), and the blog's owner sets its level and sees what it does.</p>";
        var label = form is not null && form.ContainsKey("label") ? form["label"].ToString() : DefaultLabel(request);
        return $"""
            <fieldset><legend>Blog</legend>{options}</fieldset>
            <label for="label">Label</label>
            <input id="label" name="label" maxlength="100" value="{H(label)}">
            <p class="muted">It starts at <strong>Propose</strong>: {H(Levels.Means(Levels.Propose))}</p>
            {notOwner}
            """;
    }

    public async ValueTask CreateAsync(McpConsentRequest request, McpOAuthGrant grant, string ownerId, IFormCollection form, CancellationToken ct)
    {
        if (!Guid.TryParse(ownerId, out var accountId) || !Guid.TryParse(form["blog"].ToString(), out var blogId))
            throw new McpConsentRejectedException("Pick the blog this connection is for.");
        var label = form["label"].ToString().Trim();
        if (label.Length > 100) throw new McpConsentRejectedException("Keep the label to 100 characters.");
        var created = await connections.CreateAsync(grant.ConnectionId, accountId, blogId,
            label.Length > 0 ? label : DefaultLabel(request), grant.ClientId, grant.AuthorizationId, ct, bylineName: request.ClientName);
        if (!created) throw new McpConsentRejectedException("You can only connect a blog you're on.");
    }

    public async ValueTask<ClaimsPrincipal?> ResolveAsync(McpOAuthGrant grant, CancellationToken ct)
    {
        var caller = await connections.ResolveAsync(grant.ConnectionId, grant.AuthorizationId, ct);
        return caller is null
            ? null
            : new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(BlogOAuth.ConnectionIdClaim, caller.ConnectionId.ToString()),
                new Claim(BlogOAuth.BlogIdClaim, caller.BlogId.ToString()),
                new Claim(BlogOAuth.LevelClaim, caller.Level),
                new Claim(BlogOAuth.PersonIdClaim, caller.PersonId.ToString()),
                new Claim(BlogOAuth.RoleClaim, caller.Role),
            ], "agentblog-connection"));
    }
}

/// <summary>
/// Who may approve a connection on the consent page: the signed-in owner (their <c>blog_session</c>), or else a redirect
/// to sign-in that comes back here. Allow still needs the page's own unlock proof (the package's rule), so the cookie
/// alone never grants: a cross-site post lands on the choices, never on a code.
/// </summary>
public sealed class BlogConsentOwner(Sessions sessions) : IMcpConsentOwner
{
    public async ValueTask<McpOwnerCheck> CheckAsync(McpConsentRequest request, HttpContext context, IFormCollection? form, CancellationToken ct) =>
        await sessions.AuthenticateAsync(context, ct) is { } session
            ? McpOwnerCheck.Owner(session.AccountId.ToString())
            : McpOwnerCheck.SignIn("/sign-in?returnTo=" + Uri.EscapeDataString(LocalPart(request.ReturnUrl)));

    /// <summary>The package names the consent page by its absolute URL on this issuer; sign-in carries only the path back.</summary>
    private static string LocalPart(string returnUrl) =>
        Uri.TryCreate(returnUrl, UriKind.Absolute, out var u) ? u.PathAndQuery : returnUrl;
}
