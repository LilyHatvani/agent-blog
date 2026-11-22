using System.Text.Json.Serialization;
using AgentBlog.Auth;
using AgentBlog.Database;

namespace AgentBlog.Posts;

/// <summary>
/// The owner's posts API (docs/specs/posts.md): the same <see cref="PostService"/> the MCP tools call, as the owner (no
/// level limits), plus what only the owner does: deciding approval requests and restoring old revisions.
/// </summary>
public static class PostsApi
{
    public static void MapPostsApi(this WebApplication app)
    {
        var api = app.MapOwnerApi();

        api.MapGet("/blogs/{blogId:guid}/posts", async (HttpContext ctx, Guid blogId, string? status, string? text, int? offset,
            PostService posts, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var list = await posts.ListAsync(blogId, status, text, 100, offset ?? 0, ctx.RequestAborted);
            return Results.Json(new { posts = list.Select(Json) });
        });

        api.MapGet("/posts/{id:guid}", async (HttpContext ctx, Guid id, PostService posts, Db db) =>
        {
            if (await BlogOfAsync(db, ctx, id) is not { } blogId) return NotFound();
            var post = await posts.ReadAsync(blogId, id.ToString(), ctx.RequestAborted);
            var revisions = await posts.RevisionsAsync(blogId, id.ToString(), ctx.RequestAborted);
            return Results.Json(new
            {
                post = Json(post!),
                body_md = post!.BodyMd,
                revisions = revisions!.Select(r => new { id = r.Id, number = r.Number, title = r.Title, by = r.Actor, at = r.CreatedAt }),
            });
        });

        // publish | schedule (with "at") | unpublish | trash | restore | submit
        api.MapPost("/posts/{id:guid}/{op:regex(^(publish|schedule|unpublish|trash|restore|submit)$)}",
            async (HttpContext ctx, Guid id, string op, PostService posts, Db db) =>
            {
                if (await BlogOfAsync(db, ctx, id) is not { } blogId) return NotFound();
                // The body is optional ({ "base_revision", "at" }), so it's read here: a body parameter would make the route
                // match only requests that send JSON, and a bare POST would fall through to another endpoint.
                TransitionBody? body = null;
                if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
                {
                    try { body = await ctx.Request.ReadFromJsonAsync<TransitionBody>(ctx.RequestAborted); }
                    catch (System.Text.Json.JsonException) { return SignIn.Problem(400, "invalid_argument", "The body is JSON: { \"base_revision\", \"at\" }, both optional."); }
                }
                var outcome = await posts.TransitionAsync(Actor.Owner(blogId, SignIn.Current(ctx).AccountId), id.ToString(), op,
                    body?.BaseRevision, body?.At, ctx.RequestAborted);
                return Result(outcome);
            });

        api.MapPost("/posts/{id:guid}/revisions/{revisionId:guid}/restore", async (HttpContext ctx, Guid id, Guid revisionId, PostService posts, Db db) =>
        {
            if (await BlogOfAsync(db, ctx, id) is not { } blogId) return NotFound();
            return Result(await posts.RestoreRevisionAsync(Actor.Owner(blogId, SignIn.Current(ctx).AccountId), id.ToString(), revisionId, ctx.RequestAborted));
        });

        api.MapGet("/requests", async (HttpContext ctx, PostService posts) =>
            Results.Json(new { requests = (await posts.WaitingForAsync(SignIn.Current(ctx).AccountId, ctx.RequestAborted)).Select(RequestJson) }));

        api.MapPost("/requests/{id:guid}/{decision:regex(^(approve|decline)$)}", async (HttpContext ctx, Guid id, string decision, PostService posts) =>
            Result(await posts.DecideAsync(SignIn.Current(ctx).AccountId, id, decision == "approve", ctx.RequestAborted)));
    }

    public sealed record TransitionBody(
        [property: JsonPropertyName("base_revision")] Guid? BaseRevision,
        [property: JsonPropertyName("at")] DateTimeOffset? At);

    private static IResult Result(PostOutcome o) => o.Kind switch
    {
        PostOutcome.Done => Results.Json(new
        {
            post = o.Post is { } p ? Json(p) : null,
            request = o.Request is { } q ? RequestJson(q) : null,
        }),
        _ => o.Code switch
        {
            "not_found" => SignIn.Problem(404, o.Code, o.Message!),
            "stale" or "stale_revision" or "invalid_state" => SignIn.Problem(409, o.Code, o.Message!),
            _ => SignIn.Problem(400, o.Code ?? "invalid_argument", o.Message ?? ""),
        },
    };

    private static IResult NotFound() => SignIn.Problem(404, "not_found", "No such post on your blogs.");

    private static object Json(PostView p) => new
    {
        id = p.Id, slug = p.Slug, status = p.Status, title = p.Title, excerpt = p.Excerpt, revision = p.RevisionId,
        revision_number = p.RevisionNumber, publish_at = p.PublishAt, published_at = p.PublishedAt, updated_at = p.UpdatedAt,
    };

    internal static object RequestJson(RequestView q) => new
    {
        id = q.Id, blog = q.BlogId, post = q.PostId, post_title = q.PostTitle, action = q.Action, pinned_revision = q.PinnedRevisionId,
        state = q.State, agent = q.ConnectionLabel, created_at = q.CreatedAt, decided_at = q.DecidedAt,
    };

    private static Task<bool> OwnsAsync(Db db, HttpContext ctx, Guid blogId) => db.ReadAsync(tx => tx.One<bool>("""
        SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @blogId AND account_id = @accountId AND role = 'owner')
        """, new { blogId, accountId = SignIn.Current(ctx).AccountId }), ctx.RequestAborted);

    /// <summary>The blog of a post the signed-in account owns, or null: another account's post reads as not found.</summary>
    private static Task<Guid?> BlogOfAsync(Db db, HttpContext ctx, Guid postId) => db.ReadAsync(tx => tx.One<Guid?>("""
        SELECT p.blog_id FROM post p
        JOIN blog_member m ON m.blog_id = p.blog_id AND m.account_id = @accountId AND m.role = 'owner'
        WHERE p.id = @postId
        """, new { postId, accountId = SignIn.Current(ctx).AccountId }), ctx.RequestAborted);
}
