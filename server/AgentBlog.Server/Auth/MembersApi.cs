using System.Text.Json.Serialization;
using AgentBlog.Database;
using AgentBlog.Posts;

namespace AgentBlog.Auth;

/// <summary>The owner's People screen (docs/specs/members.md): who is on the blog, invitations, roles, removal. Owner only.</summary>
public static class MembersApi
{
    public sealed record InviteBody([property: JsonPropertyName("role")] string? Role, [property: JsonPropertyName("note")] string? Note);

    public sealed record RoleBody([property: JsonPropertyName("role")] string? Role);

    public static void MapMembersApi(this WebApplication app)
    {
        var api = app.MapOwnerApi();

        api.MapGet("/blogs/{blogId:guid}/people", async (HttpContext ctx, Guid blogId, Members members, TimeProvider clock) =>
        {
            if (await OwnerAsync(ctx, members, blogId) is null) return NotFound();
            var now = clock.GetUtcNow().UtcDateTime;
            return Results.Json(new
            {
                people = (await members.PeopleAsync(blogId, ctx.RequestAborted)).Select(p => new
                {
                    account = p.AccountId, name = p.DisplayName, role = p.Role, joined_at = p.JoinedAt,
                }),
                invitations = (await members.InvitationsAsync(blogId, ctx.RequestAborted)).Select(i => new
                {
                    id = i.Id, role = i.Role, note = i.Note, state = i.State(now), created_at = i.CreatedAt, expires_at = i.ExpiresAt,
                    used_at = i.UsedAt, used_by = i.UsedByName,
                }),
            });
        });

        api.MapPost("/blogs/{blogId:guid}/invitations", async (HttpContext ctx, Guid blogId, InviteBody body, Members members, IConfiguration config) =>
        {
            if (await OwnerAsync(ctx, members, blogId) is not { } owner) return NotFound();
            var (invitation, token, error) = await members.InviteAsync(owner, body.Role ?? "", body.Note, ctx.RequestAborted);
            if (invitation is null) return SignIn.Problem(400, "invalid_argument", error!);
            var origin = config["Blog:PublicUrl"] is { Length: > 0 } u ? u.TrimEnd('/') : $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            // The link is shown once: only its hash is kept.
            return Results.Json(new { id = invitation.Id, role = invitation.Role, expires_at = invitation.ExpiresAt, link = $"{origin}/invite/{token}" }, statusCode: 201);
        });

        api.MapPost("/blogs/{blogId:guid}/invitations/{id:guid}/withdraw", async (HttpContext ctx, Guid blogId, Guid id, Members members) =>
        {
            if (await OwnerAsync(ctx, members, blogId) is not { } owner) return NotFound();
            return await members.WithdrawAsync(owner, id, ctx.RequestAborted)
                ? Results.NoContent() : SignIn.Problem(409, "not_now", "That invitation is used, withdrawn already, or not this blog's.");
        });

        api.MapPatch("/blogs/{blogId:guid}/people/{accountId:guid}", async (HttpContext ctx, Guid blogId, Guid accountId, RoleBody body, Members members) =>
        {
            if (await OwnerAsync(ctx, members, blogId) is not { } owner) return NotFound();
            return await members.SetRoleAsync(owner, accountId, body.Role ?? "", ctx.RequestAborted)
                ? Results.NoContent() : SignIn.Problem(400, "invalid_argument", "role is editor or writer, for someone on the blog who isn't its owner.");
        });

        api.MapDelete("/blogs/{blogId:guid}/people/{accountId:guid}", async (HttpContext ctx, Guid blogId, Guid accountId, Members members,
            AgentKit.McpOAuth.IMcpOAuthGrants oauth) =>
        {
            if (await OwnerAsync(ctx, members, blogId) is not { } owner) return NotFound();
            if (await members.RemoveAsync(owner, accountId, ctx.RequestAborted) is not { } grants)
                return SignIn.Problem(404, "not_found", "No such person on this blog (the owner can't be removed).");
            // Their agents' tokens end too, so no client refreshes its way back in (a call would find no blog anyway).
            foreach (var grant in grants) await oauth.RevokeAsync(grant, ctx.RequestAborted);
            return Results.NoContent();
        });
    }

    private static IResult NotFound() => SignIn.Problem(404, "not_found", "No such blog of yours.");

    /// <summary>The signed-in owner of <paramref name="blogId"/>, or null (anyone else, an editor included, finds nothing here).</summary>
    private static async Task<Actor?> OwnerAsync(HttpContext ctx, Members members, Guid blogId) =>
        await members.OnBlogAsync(blogId, SignIn.Current(ctx).AccountId, ctx.RequestAborted) is { IsOwner: true } owner ? owner : null;
}
