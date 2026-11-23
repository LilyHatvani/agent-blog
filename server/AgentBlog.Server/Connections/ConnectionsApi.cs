using System.Text.Json.Serialization;
using AgentBlog.Auth;
using AgentKit.McpOAuth;

namespace AgentBlog.Connections;

/// <summary>
/// The owner's connections API (docs/specs/connections.md): list, set a level, revoke. Session-only, behind the CSRF
/// header: an agent's bearer token never reaches it, so no agent changes a level or a connection.
/// </summary>
public static class ConnectionsApi
{
    public static void MapConnectionsApi(this WebApplication app)
    {
        var api = app.MapOwnerApi();

        api.MapGet("/connections", async (HttpContext ctx, ConnectionStore connections) =>
            Results.Json(new
            {
                connections = (await connections.ListAsync(SignIn.Current(ctx).AccountId, ctx.RequestAborted)).Select(c => new
                {
                    id = c.Id,
                    blog = new { id = c.BlogId, title = c.BlogTitle },
                    label = c.Label,
                    level = c.Level,
                    level_means = Levels.Means(c.Level),
                    // Whose agent it is, and the most they let it do (docs/specs/members.md): it works at the lower.
                    person = new { id = c.PersonId, name = c.PersonName, role = c.PersonRole },
                    mine = c.Mine,
                    cap = c.Cap,
                    works_at = c.WorksAt,
                    can_set_level = c.ViewerOwns,
                    client_id = c.ClientId,
                    byline_name = c.BylineName,
                    byline_name_set = c.BylineNameSet,
                    created_at = c.CreatedAt,
                    last_used_at = c.LastUsedAt,
                }),
                levels = Levels.All.Select(l => new { level = l, means = Levels.Means(l) }),
            }));

        // { "level": "propose" | "publish" | "full", "confirm_full": true }. Full needs the confirmation (decision 0001).
        // { "byline_name": "Claude" } sets its name on posts ("" goes back to its client's name; docs/specs/bylines.md).
        api.MapPatch("/connections/{id:guid}", async (HttpContext ctx, Guid id, LevelBody body, ConnectionStore connections) =>
        {
            var account = SignIn.Current(ctx).AccountId;
            if (body.BylineName is { } name)
            {
                var named = await connections.SetBylineNameAsync(account, id, name, ctx.RequestAborted);
                if (named == ConnectionStore.Outcome.Invalid) return SignIn.Problem(400, "invalid_argument", "A name on posts is at most 60 characters, on one line.");
                if (named != ConnectionStore.Outcome.Done) return SignIn.Problem(404, "not_found", "No such connection on your blogs.");
                if (body.Level is null) return Results.NoContent();
            }
            return await connections.SetLevelAsync(account, id, body.Level ?? "", body.ConfirmFull == true, ctx.RequestAborted) switch
            {
                ConnectionStore.Outcome.Done => Results.NoContent(),
                ConnectionStore.Outcome.NeedsConfirmation => SignIn.Problem(409, "confirm_full",
                    "Full lets this agent publish, change and trash posts without asking you. Send confirm_full: true to give it that."),
                ConnectionStore.Outcome.Invalid => SignIn.Problem(400, "invalid_argument", "level is propose, publish or full."),
                ConnectionStore.Outcome.AboveCap => SignIn.Problem(400, "above_person",
                    "An agent never does more than its person may: a writer's agent stays at Propose, an editor's goes at most to Publish."),
                _ => SignIn.Problem(404, "not_found", "No such connection on your blogs."),
            };
        });

        // Revoking ends the connection and every token under its grant, so the client can't refresh its way back in.
        api.MapDelete("/connections/{id:guid}", async (HttpContext ctx, Guid id, ConnectionStore connections, IMcpOAuthGrants grants) =>
        {
            var (outcome, authorization) = await connections.RevokeAsync(SignIn.Current(ctx).AccountId, id, ctx.RequestAborted);
            if (outcome != ConnectionStore.Outcome.Done) return SignIn.Problem(404, "not_found", "No such connection on your blogs.");
            if (authorization is not null) await grants.RevokeAsync(authorization, ctx.RequestAborted);
            return Results.NoContent();
        });
    }

    public sealed record LevelBody(
        [property: JsonPropertyName("level")] string? Level,
        [property: JsonPropertyName("confirm_full")] bool? ConfirmFull,
        [property: JsonPropertyName("byline_name")] string? BylineName = null);
}
