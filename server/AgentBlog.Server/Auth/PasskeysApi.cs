using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBlog.Database;

namespace AgentBlog.Auth;

/// <summary>
/// The passkey endpoints (docs/specs/auth.md § Passkeys). Each ceremony is two calls: <c>begin</c> answers what the
/// browser should sign (WebAuthn options) and a ceremony id; <c>finish</c> takes the browser's answer for that id once.
/// <list type="bullet">
/// <item><c>POST /api/auth/passkey/begin</c> <c>{ "purpose": "setup" | "invite" | "sign-in", "token"?, "name"?, "return_to"? }</c>
/// and <c>POST /api/auth/passkey/finish</c> <c>{ "ceremony", "credential" }</c>: before a session. JSON only, rate
/// limited like the provider sign-ins.</item>
/// <item><c>POST /api/auth/passkeys/begin</c> <c>{ }</c> and <c>/finish</c>: a signed-in person adds a passkey.</item>
/// </list>
/// </summary>
public static class PasskeysApi
{
    public static void MapPasskeys(this WebApplication app)
    {
        app.MapPost("/api/auth/passkey/begin", BeginAsync).WithMetadata(SignIn.AuthRateLimited.Instance);
        app.MapPost("/api/auth/passkey/finish", FinishAsync).WithMetadata(SignIn.AuthRateLimited.Instance);

        var api = app.MapOwnerApi();
        api.MapPost("/auth/passkeys/begin", async (HttpContext ctx, Passkeys passkeys, Accounts accounts) =>
        {
            var session = SignIn.Current(ctx);
            var me = await accounts.MeAsync(session.AccountId, ctx.RequestAborted);
            if (me is null) return SignIn.Problem(401, "unauthenticated", "Sign in again.");
            var begun = await passkeys.BeginCreateAsync(ctx, Passkeys.PurposeAdd, $"{session.AccountId:D}:{session.Id}", me.DisplayName,
                session.AccountId, ctx.RequestAborted);
            return Results.Json(new { ceremony = begun.Ceremony, options = begun.Options });
        }).WithMetadata(SignIn.AuthRateLimited.Instance);

        api.MapPost("/auth/passkeys/finish", async (HttpContext ctx, Passkeys passkeys, Accounts accounts, Sessions sessions) =>
        {
            if (await ReadAsync<FinishBody>(ctx) is not { Ceremony: { } id, Credential: { } credential })
                return SignIn.Problem(400, "invalid_argument", "Send { \"ceremony\": \"…\", \"credential\": { … } }.");
            var session = SignIn.Current(ctx);
            if (await passkeys.TakeAsync(id, ctx.RequestAborted) is not { Purpose: Passkeys.PurposeAdd } c
                || c.Data != $"{session.AccountId:D}:{session.Id}" || c.UserHandle is not { } handle)
                return Gone();
            if (!await sessions.IsLiveAsync(session.Id, session.AccountId, ctx.RequestAborted))
                return SignIn.Problem(401, "unauthenticated", "Sign in again.");
            var (created, why) = await passkeys.VerifyCreatedAsync(ctx, c, credential.ToJsonString());
            if (created is null) return NotAccepted(why);
            if (!await accounts.BindAnotherAsync(session.AccountId, created.Identity, ctx.RequestAborted))
                return SignIn.Problem(409, "identity_taken", "That passkey already belongs to someone else here.");
            await passkeys.SaveAsync(session.AccountId, created, handle, ctx.RequestAborted);
            return Results.Json(new { provider = Passkeys.Provider, account_id = created.Identity.AccountId }, statusCode: 201);
        }).WithMetadata(SignIn.AuthRateLimited.Instance);
    }

    private sealed record BeginBody(string? Purpose, string? Token, string? Name, string? ReturnTo);

    private sealed record FinishBody(string? Ceremony, JsonObject? Credential);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static async Task<T?> ReadAsync<T>(HttpContext ctx) where T : class
    {
        if (!ctx.Request.HasJsonContentType()) return null;
        try { return await ctx.Request.ReadFromJsonAsync<T>(Json, ctx.RequestAborted); }
        catch (JsonException) { return null; }
    }

    private static IResult Gone() =>
        SignIn.Problem(410, "ceremony_gone", "That took too long, or was answered already. Start again.");

    private static IResult NotAccepted(string? why) =>
        SignIn.Problem(400, "passkey_rejected", $"The passkey wasn't accepted: {why}");

    /// <summary>A name as readers see it on posts: trimmed, 1–100 characters.</summary>
    private static string? CleanName(string? name) =>
        name?.Trim() is { Length: > 0 and <= Passkeys.MaxName } n ? n : null;

    private static async Task<IResult> BeginAsync(HttpContext ctx, Passkeys passkeys, Accounts accounts, Members members, IConfiguration config)
    {
        if (await ReadAsync<BeginBody>(ctx) is not { Purpose: { } purpose } body)
            return SignIn.Problem(400, "invalid_argument", "Send { \"purpose\": \"setup\", \"invite\" or \"sign-in\", … } as application/json.");
        Passkeys.Begun begun;
        switch (purpose)
        {
            case Passkeys.PurposeSetup:
            {
                var (hash, problem) = await SignIn.CheckSetupTokenAsync(body.Token, config, accounts, ctx.RequestAborted);
                if (problem is not null) return problem;
                if (CleanName(body.Name) is not { } name)
                    return SignIn.Problem(400, "invalid_argument", $"Give your name as readers will see it on posts (1–{Passkeys.MaxName} characters).");
                // A re-opened setup (recovery) adds the passkey to the owner there is.
                begun = await passkeys.BeginCreateAsync(ctx, purpose, Convert.ToHexStringLower(hash!), name,
                    await accounts.OwnerIdAsync(ctx.RequestAborted), ctx.RequestAborted);
                break;
            }
            case Passkeys.PurposeInvite:
            {
                if (body.Token is not { Length: >= 16 and <= 100 } token || await members.LookUpAsync(token, ctx.RequestAborted) is null)
                    return SignIn.Problem(410, "invitation_gone", "That invitation has run out, was used, or was withdrawn. Ask the owner for a new one.");
                if (CleanName(body.Name) is not { } name)
                    return SignIn.Problem(400, "invalid_argument", $"Give your name as readers will see it on posts (1–{Passkeys.MaxName} characters).");
                begun = await passkeys.BeginCreateAsync(ctx, purpose, Convert.ToHexStringLower(Members.Hash(token)), name, null, ctx.RequestAborted);
                break;
            }
            case Passkeys.PurposeSignIn:
                begun = await passkeys.BeginSignInAsync(ctx, Pages.LocalPath(body.ReturnTo), ctx.RequestAborted);
                break;
            default:
                return SignIn.Problem(400, "invalid_argument", "The purpose is \"setup\", \"invite\" or \"sign-in\".");
        }
        return Results.Json(new { ceremony = begun.Ceremony, options = begun.Options });
    }

    private static async Task<IResult> FinishAsync(HttpContext ctx, Passkeys passkeys, Accounts accounts, Members members, Sessions sessions,
        IConfiguration config)
    {
        if (await ReadAsync<FinishBody>(ctx) is not { Ceremony: { } id, Credential: { } credential })
            return SignIn.Problem(400, "invalid_argument", "Send { \"ceremony\": \"…\", \"credential\": { … } } as application/json.");
        if (await passkeys.TakeAsync(id, ctx.RequestAborted) is not { } c || c.Purpose == Passkeys.PurposeAdd) return Gone();
        var json = credential.ToJsonString();
        var ct = ctx.RequestAborted;

        switch (c.Purpose)
        {
            case Passkeys.PurposeSetup:
            {
                // Still the configured token? A token changed or removed meanwhile claims nothing.
                if (SignIn.SetupToken(config) is not { } token || Convert.ToHexStringLower(Sessions.Hash(token)) != c.Data)
                    return SignIn.Problem(409, "setup_closed", "Setup closed while you were creating the passkey. If this blog has an owner, sign in instead.");
                var (created, why) = await passkeys.VerifyCreatedAsync(ctx, c, json);
                if (created is null) return NotAccepted(why);
                if (await accounts.CompleteSetupAsync(created.Identity, Sessions.Hash(token), ct) is not { } owner)
                    return SignIn.Problem(409, "setup_closed", "Setup closed while you were creating the passkey. If this blog has an owner, sign in instead.");
                await passkeys.SaveAsync(owner, created, c.UserHandle!, ct);
                await sessions.StartAsync(ctx, owner, Passkeys.Provider, created.Identity.AccountId, ct);
                return Results.Json(new { redirect = "/admin" });
            }
            case Passkeys.PurposeInvite:
            {
                if (c.Data is not { Length: 64 } hex) return Gone();
                var (created, why) = await passkeys.VerifyCreatedAsync(ctx, c, json);
                if (created is null) return NotAccepted(why);
                var (outcome, joined) = await members.AcceptAsync(Convert.FromHexString(hex), created.Identity, ct);
                if (outcome != Members.Accepted.Joined || joined is not { } accountId)
                    return SignIn.Problem(410, "invitation_gone", "That invitation has run out, was used, or was withdrawn. Ask the owner for a new one.");
                await passkeys.SaveAsync(accountId, created, c.UserHandle!, ct);
                await sessions.StartAsync(ctx, accountId, Passkeys.Provider, created.Identity.AccountId, ct);
                return Results.Json(new { redirect = "/admin?joined=1" });
            }
            default:
            {
                var (identity, why) = await passkeys.VerifySignInAsync(ctx, c, json, ct);
                if (identity is null) return NotAccepted(why);
                if (await accounts.SignInAsync(identity, ct) is not { } accountId)
                    return SignIn.Problem(403, "not_owner", "That passkey isn't on this blog any more. Ask the owner for an invitation.");
                await sessions.StartAsync(ctx, accountId, Passkeys.Provider, identity.AccountId, ct);
                return Results.Json(new { redirect = c.Data ?? "/admin" });
            }
        }
    }
}
