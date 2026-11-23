using System.Text.Json;
using System.Threading.RateLimiting;
using AgentKit.ProviderSignIn;
using AgentBlog.Database;

namespace AgentBlog.Auth;

/// <summary>
/// Owner sign-in with Google or Discord (docs/specs/auth.md), on <c>AgentKit.ProviderSignIn</c> (decision 0007). The
/// package runs the provider round trip and hands back a verified (provider, account id); <see cref="Completion"/>
/// decides who that is:
/// <list type="bullet">
/// <item><b>Setup</b>: <c>Blog__SetupToken</c> plus a provider sign-in creates the owner and the blog, then burns the
/// token. A new token re-opens setup, which binds the new sign-in to the same owner (recovery).</item>
/// <item><b>Sign-in</b>: only a bound (provider, account id) gets in.</item>
/// <item><b>Bind</b>: a signed-in owner adds the other provider, which doubles as a second way in.</item>
/// </list>
/// Shaped after Relay's <c>Api/OwnerSignIn.cs</c>.
/// </summary>
public static class SignIn
{
    public const string SetupTokenKey = "Blog:SetupToken";
    public const int SetupTokenMinLength = 32;
    public const string PurposeSetup = "setup";
    public const string PurposeInvite = "invite";
    public const string PurposeBind = "bind";

    /// <summary>The header every cookie-authenticated write must carry. A cross-site form can't set it, and a cross-site
    /// fetch with it needs a CORS preflight this server never grants.</summary>
    public const string CsrfHeader = "X-Blog-Request";

    public static IServiceCollection AddBlogSignIn(this IServiceCollection services, IConfiguration config, ConnectionSettings connections)
    {
        services.AddProviderSignIn(o =>
        {
            o.ConnectionString = connections.App;
            config.GetSection("Blog:Auth:Google").Bind(o.Google);
            config.GetSection("Blog:Auth:Discord").Bind(o.Discord);
            // The redirect's base, required (https, or http on loopback) only once a provider is configured.
            if (o.Google.IsConfigured || o.Discord.IsConfigured)
                o.PublicUrl = config["Blog:PublicUrl"] is { Length: > 0 } url ? url : null;
        });
        services.AddSingleton<Sessions>();
        services.AddSingleton<Accounts>();
        services.AddSingleton<Members>();
        services.AddSingleton<Passkeys>();
        services.AddSingleton<IProviderSignInCompletion, Completion>();

        var perMinute = config.GetValue("Blog:Auth:RateLimitPerMinute", 10);
        var overall = config.GetValue("Blog:Auth:GlobalRateLimitPerMinute", Math.Max(60, perMinute));
        var hops = config.GetValue("Blog:Proxy:ForwardedForHops", 0);
        services.AddRateLimiter(o =>
        {
            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => IsAuth(ctx)
                    ? RateLimitPartition.GetFixedWindowLimiter("client:" + ClientAddress(ctx, hops), _ => Window(perMinute))
                    : RateLimitPartition.GetNoLimiter("")),
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => IsAuth(ctx)
                    ? RateLimitPartition.GetFixedWindowLimiter("all", _ => Window(overall))
                    : RateLimitPartition.GetNoLimiter("")));
            o.OnRejected = async (rejected, ct) =>
            {
                rejected.HttpContext.Response.Headers.RetryAfter = "60";
                await Problem(429, "too_many_requests",
                    $"Too many sign-in attempts from here: at most {perMinute} a minute. Try again in a minute.")
                    .ExecuteAsync(rejected.HttpContext);
            };
        });
        return services;
    }

    public static void MapBlogSignIn(this WebApplication app)
    {
        app.MapProviderSignIn().WithMetadata(AuthRateLimited.Instance);
        app.MapPost("/api/auth/setup", SetupAsync).WithMetadata(AuthRateLimited.Instance);
        app.MapPost("/api/auth/invite", InviteAsync).WithMetadata(AuthRateLimited.Instance);
        app.MapPasskeys();

        var api = app.MapOwnerApi();
        api.MapGet("/me", async (HttpContext ctx, Accounts accounts, ProviderSignInFlow flow) =>
        {
            var session = Current(ctx);
            var me = await accounts.MeAsync(session.AccountId, ctx.RequestAborted);
            if (me is null) return Problem(401, "unauthenticated", "Sign in again.");
            var identities = await accounts.IdentitiesAsync(session.AccountId, ctx.RequestAborted);
            return Results.Json(new
            {
                account = new { id = me.Id, display_name = me.DisplayName },
                // Their author page on each blog (docs/specs/authors.md), shown to readers once something of theirs is live.
                blogs = me.Blogs.Select(b => new { id = b.Id, slug = b.Slug, title = b.Title, role = b.Role, author_path = b.AuthorSlug is { } a ? $"/author/{a}" : null,
                    author_profile_url = b.AuthorProfileUrl }),
                identities = identities.Select(i => new
                {
                    provider = i.Provider,
                    account_id = i.ProviderAccountId,
                    display_name = i.DisplayName,
                    created_at = i.CreatedAt,
                    this_session = i.Provider == session.Provider && i.ProviderAccountId == session.ProviderAccountId,
                }),
                providers = flow.EnabledProviders,
            });
        });

        // Begins a sign-in that binds the provider account it returns to this account. JSON, then the page navigates.
        api.MapPost("/auth/identities/{provider}", async (HttpContext ctx, string provider, ProviderSignInFlow flow) =>
        {
            if (!flow.IsEnabled(provider)) return Problem(400, "invalid_argument", NotOffered(flow, provider));
            var session = Current(ctx);
            var url = await flow.BeginAsync(provider, ctx,
                new ProviderSignInContext(PurposeBind, $"{session.AccountId:D}:{session.Id}", "/admin"), ctx.RequestAborted);
            return Results.Json(new { url = url.AbsoluteUri });
        }).WithMetadata(AuthRateLimited.Instance);

        api.MapDelete("/auth/identities/{provider}/{providerAccountId}",
            async (HttpContext ctx, string provider, string providerAccountId, Accounts accounts) =>
                await accounts.UnbindAsync(Current(ctx).AccountId, provider, providerAccountId, ctx.RequestAborted) switch
                {
                    Accounts.UnbindResult.Unbound => Results.NoContent(),
                    Accounts.UnbindResult.LastOne => Problem(409, "last_identity",
                        "This is your only way to sign in. Add another one first, then remove this one."),
                    _ => Problem(404, "not_found", "No such sign-in on your account."),
                });

        api.MapPost("/auth/sign-out", async (HttpContext ctx, Sessions sessions) =>
        {
            await sessions.EndAsync(ctx, ctx.RequestAborted);
            return Results.NoContent();
        });

        api.MapPost("/auth/sign-out-everywhere", async (HttpContext ctx, Sessions sessions) =>
            Results.Json(new { ended = await sessions.EndAllAsync(ctx, Current(ctx).AccountId, ctx.RequestAborted) }));
    }

    /// <summary>
    /// <c>POST /api/auth/setup</c> <c>{ "token", "provider" }</c>: checks the setup token, then begins the sign-in that
    /// claims the blog, and answers <c>{ "url" }</c>. The token burns only when that sign-in completes, so cancelling at the
    /// provider costs nothing. JSON only: a cross-site form can't send it.
    /// </summary>
    private static async Task<IResult> SetupAsync(HttpContext ctx, ProviderSignInFlow flow, Accounts accounts, IConfiguration config)
    {
        if (!ctx.Request.HasJsonContentType())
            return Problem(415, "invalid_argument", "Send the setup as application/json.");
        SetupBody? body;
        try { body = await ctx.Request.ReadFromJsonAsync<SetupBody>(ctx.RequestAborted); }
        catch (JsonException) { body = null; }
        if (body is not { Token: { } given, Provider: { } provider })
            return Problem(400, "invalid_argument", "Send { \"token\": \"…\", \"provider\": \"google\" or \"discord\" }.");

        if (!flow.IsEnabled(provider)) return Problem(400, "invalid_argument", NotOffered(flow, provider));
        var (hash, problem) = await CheckSetupTokenAsync(given, config, accounts, ctx.RequestAborted);
        if (problem is not null) return problem;

        var url = await flow.BeginAsync(provider, ctx,
            new ProviderSignInContext(PurposeSetup, Convert.ToHexStringLower(hash!), "/admin"), ctx.RequestAborted);
        return Results.Json(new { url = url.AbsoluteUri });
    }

    /// <summary>
    /// The setup token's checks, shared by every way to claim the blog (a provider or a passkey): configured, the one
    /// given, and setup still open for it. Returns its hash, or the refusal.
    /// </summary>
    internal static async Task<(byte[]? Hash, IResult? Problem)> CheckSetupTokenAsync(string? given, IConfiguration config, Accounts accounts,
        CancellationToken ct)
    {
        if (SetupToken(config) is not { } expected)
            return (null, Problem(409, "setup_closed",
                $"Setup is closed: Blog__SetupToken is not set on the server (at least {SetupTokenMinLength} characters)."));
        if (given is null || !SameSecret(given, expected))
            return (null, Problem(401, "unauthenticated", "That is not the setup token (Blog__SetupToken)."));
        var hash = Sessions.Hash(expected);
        if (!await accounts.SetupOpenAsync(hash, ct))
            return (null, Problem(409, "setup_done",
                "Setup is done: this blog has its owner. Sign in instead. Lost access? Set a new Blog__SetupToken and run setup again."));
        return (hash, null);
    }

    private sealed record SetupBody(string? Token, string? Provider);

    /// <summary>
    /// <c>POST /api/auth/invite</c> <c>{ "token", "provider" }</c>: checks the invitation still works, then begins the
    /// sign-in that accepts it (docs/specs/members.md). The invitation is spent only when that sign-in completes.
    /// </summary>
    private static async Task<IResult> InviteAsync(HttpContext ctx, ProviderSignInFlow flow, Members members)
    {
        if (!ctx.Request.HasJsonContentType()) return Problem(415, "invalid_argument", "Send it as application/json.");
        SetupBody? body;
        try { body = await ctx.Request.ReadFromJsonAsync<SetupBody>(ctx.RequestAborted); }
        catch (JsonException) { body = null; }
        if (body is not { Token: { } token, Provider: { } provider })
            return Problem(400, "invalid_argument", "Send { \"token\": \"…\", \"provider\": \"google\" or \"discord\" }.");
        if (!flow.IsEnabled(provider)) return Problem(400, "invalid_argument", NotOffered(flow, provider));
        if (await members.LookUpAsync(token, ctx.RequestAborted) is null)
            return Problem(410, "invitation_gone", "That invitation has run out, was used, or was withdrawn. Ask the owner for a new one.");
        var url = await flow.BeginAsync(provider, ctx,
            new ProviderSignInContext(PurposeInvite, Convert.ToHexStringLower(Members.Hash(token)), "/admin"), ctx.RequestAborted);
        return Results.Json(new { url = url.AbsoluteUri });
    }

    /// <summary>The configured setup token, or null when unset or too short to be safe.</summary>
    public static string? SetupToken(IConfiguration config) =>
        config[SetupTokenKey] is { Length: >= SetupTokenMinLength } t ? t : null;

    private static bool SameSecret(string given, string expected) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Sessions.Hash(given), Sessions.Hash(expected));

    /// <summary>The owner's API: a route group under <c>/api</c> that needs a session, and <see cref="CsrfHeader"/> on writes.</summary>
    public static RouteGroupBuilder MapOwnerApi(this WebApplication app) => app.MapGroup("/api").AddEndpointFilter(RequireSessionAsync);

    /// <summary>Every <c>/api</c> endpoint but the sign-in ones needs a session; every write also needs <see cref="CsrfHeader"/>.</summary>
    private static async ValueTask<object?> RequireSessionAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var ctx = invocation.HttpContext;
        var session = await ctx.RequestServices.GetRequiredService<Sessions>().AuthenticateAsync(ctx, ctx.RequestAborted);
        if (session is null) return Problem(401, "unauthenticated", "Sign in first.");
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
            && !ctx.Request.Headers.ContainsKey(CsrfHeader))
            return Problem(403, "csrf", $"A write needs the {CsrfHeader} header.");
        return await next(invocation);
    }

    internal static Sessions.Session Current(HttpContext ctx) =>
        (Sessions.Session)ctx.Items[typeof(Sessions.Session)]!;

    internal static IResult Problem(int status, string code, string detail) =>
        Results.Json(new { code, detail }, statusCode: status, contentType: "application/problem+json");

    private static string NotOffered(ProviderSignInFlow flow, string provider) => flow.EnabledProviders.Count == 0
        ? "No sign-in provider is configured on this server (Blog__Auth__Google__ClientId and __ClientSecret, or the same for Discord)."
        : $"'{provider}' is not offered here. Offered: {string.Join(", ", flow.EnabledProviders)}.";

    private static bool IsAuth(HttpContext ctx) => ctx.GetEndpoint()?.Metadata.GetMetadata<AuthRateLimited>() is not null;

    private static FixedWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };

    /// <summary>
    /// The client's address. Behind a proxy (Railway), the socket's peer is the proxy, so with
    /// <c>Blog__Proxy__ForwardedForHops</c> = N the address is the Nth entry from the right of X-Forwarded-For: the one the
    /// trusted proxy itself appended. Entries further left are the client's own claims and are never used.
    /// </summary>
    internal static string ClientAddress(HttpContext ctx, int hops)
    {
        if (hops > 0 && ctx.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } xff)
        {
            var entries = xff.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (entries.Length >= hops) return entries[^hops];
        }
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    internal sealed class AuthRateLimited
    {
        public static readonly AuthRateLimited Instance = new();
    }

    /// <summary>The blog's half of a provider callback: who the verified account is, by the purpose the sign-in began with.</summary>
    public sealed class Completion(Accounts accounts, Sessions sessions, IConfiguration config, Members members) : IProviderSignInCompletion
    {
        public async ValueTask<IResult> SignedInAsync(ProviderSignInSuccess success, HttpContext context, CancellationToken ct)
        {
            var (identity, signIn) = (success.Identity, success.Context);
            switch (signIn.Purpose)
            {
                case PurposeSetup:
                {
                    // Still the configured token? A token changed or removed while the sign-in was at the provider binds nothing.
                    if (SetupToken(config) is not { } token || Convert.ToHexStringLower(Sessions.Hash(token)) != signIn.Data)
                        return Results.Redirect("/setup?error=setup_closed");
                    if (await accounts.CompleteSetupAsync(identity, Sessions.Hash(token), ct) is not { } owner)
                        return Results.Redirect("/setup?error=setup_closed");
                    await sessions.StartAsync(context, owner, identity.Provider, identity.AccountId, ct);
                    return Results.Redirect("/admin");
                }

                case PurposeInvite:
                {
                    if (signIn.Data is not { Length: 64 } hex) return Results.Redirect("/sign-in?error=invitation_gone");
                    var (outcome, accountId) = await members.AcceptAsync(Convert.FromHexString(hex), identity, ct);
                    if (outcome == Members.Accepted.Gone || accountId is not { } joined) return Results.Redirect("/sign-in?error=invitation_gone");
                    // Joined, or already on the blog (the link stays for whoever it was meant for): either way, signed in.
                    await sessions.StartAsync(context, joined, identity.Provider, identity.AccountId, ct);
                    return Results.Redirect(outcome == Members.Accepted.Joined ? "/admin?joined=1" : "/admin");
                }

                case PurposeBind:
                {
                    if (signIn.Data?.Split(':', 2) is not [var account, var sessionId] || !Guid.TryParse(account, out var accountId)
                        || !await sessions.IsLiveAsync(sessionId, accountId, ct))
                        return Results.Redirect("/sign-in?error=session_ended");
                    return await accounts.BindAnotherAsync(accountId, identity, ct)
                        ? Results.Redirect("/admin?bound=" + identity.Provider)
                        : Results.Redirect("/admin?error=identity_taken");
                }

                case ProviderSignInPurposes.SignIn:
                {
                    if (await accounts.SignInAsync(identity, ct) is not { } accountId)
                        return Results.Redirect("/sign-in?error=not_owner");
                    await sessions.StartAsync(context, accountId, identity.Provider, identity.AccountId, ct);
                    return Results.Redirect(signIn.ReturnTo ?? "/admin");
                }

                default:
                    return Results.Redirect("/sign-in?error=state_invalid");
            }
        }

        public ValueTask<IResult> FailedAsync(ProviderSignInFailure failure, HttpContext context, CancellationToken ct) =>
            ValueTask.FromResult(Results.Redirect(failure.Context?.Purpose switch
            {
                PurposeSetup => "/setup?error=" + failure.Code,
                PurposeInvite => "/sign-in?error=" + failure.Code,
                PurposeBind => "/admin?error=" + failure.Code,
                _ => "/sign-in?error=" + failure.Code,
            }));
    }
}
