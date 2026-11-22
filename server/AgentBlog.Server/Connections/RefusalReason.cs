using AgentBlog.Database;
using OpenIddict.Abstractions;

namespace AgentBlog.Connections;

/// <summary>
/// When the MCP door refuses a bearer (401), say why in the challenge (<c>error_description</c>), so an agent can tell
/// "the owner revoked this connection" from "the token expired, refresh it" (docs/specs/connections.md § Revoking). The
/// gate (<c>AgentKit.McpOAuth</c>) decides; this only looks the token up afterwards and adds the reason, for /mcp only.
/// </summary>
public static class RefusalReason
{
    public static void UseRefusalReasons(this WebApplication app) => app.Use(async (ctx, next) =>
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (ctx.Request.Path.StartsWithSegments("/mcp") && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = header["Bearer ".Length..].Trim();
            ctx.Response.OnStarting(async () =>
            {
                if (ctx.Response.StatusCode != StatusCodes.Status401Unauthorized) return;
                var why = await WhyAsync(ctx.RequestServices, token, ctx.RequestAborted);
                var challenge = ctx.Response.Headers.WWWAuthenticate.ToString();
                if (challenge.Length == 0 || challenge.Contains("error_description", StringComparison.Ordinal)) return;
                ctx.Response.Headers.WWWAuthenticate = challenge + $", error_description=\"{why}\"";
            });
        }
        await next();
    });

    private static async Task<string> WhyAsync(IServiceProvider services, string reference, CancellationToken ct)
    {
        const string unknown = "This access token isn't valid here. Connect again.";
        try
        {
            var tokens = services.GetRequiredService<IOpenIddictTokenManager>();
            var token = await tokens.FindByReferenceIdAsync(reference, ct);
            if (token is null) return unknown;
            if (await tokens.GetAuthorizationIdAsync(token, ct) is { } authorizationId)
            {
                var revoked = await services.GetRequiredService<Db>().ReadAsync(tx => tx.One<bool>("""
                    SELECT EXISTS (SELECT 1 FROM connection WHERE oauth_authorization_id = @authorizationId AND revoked_at IS NOT NULL)
                    """, new { authorizationId }), ct);
                if (revoked) return "The blog's owner revoked this connection. To use it again, the owner connects it again.";
            }
            if (await tokens.GetExpirationDateAsync(token, ct) is { } expires && expires <= DateTimeOffset.UtcNow)
                return "The access token expired. Refresh it with the refresh token.";
            if (await tokens.HasStatusAsync(token, OpenIddictConstants.Statuses.Revoked, ct))
                return "This access token was revoked. Connect again.";
            return unknown;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return unknown; // the reason is a courtesy: never let it turn a 401 into a 500
        }
    }
}
