using System.Text.Json.Serialization;
using AgentBlog.Auth;
using AgentBlog.Database;
using AgentBlog.Public;

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
                // The preview is the same sanitized HTML readers get (Public/Markdown.cs).
                html = Markdown.ToHtml(post.BodyMd ?? ""),
                revisions = revisions!.Select(r => new { id = r.Id, number = r.Number, title = r.Title, by = r.Actor, by_label = r.ActorLabel, at = r.CreatedAt }),
            });
        });

        api.MapGet("/posts/{id:guid}/revisions/{revisionId:guid}", async (HttpContext ctx, Guid id, Guid revisionId, PostService posts, Db db) =>
        {
            if (await BlogOfAsync(db, ctx, id) is not { } blogId) return NotFound();
            if (await posts.RevisionAsync(blogId, id, revisionId, ctx.RequestAborted) is not { } r)
                return SignIn.Problem(404, "not_found", "That revision isn't one of this post's.");
            return Results.Json(new
            {
                id = r.Id, number = r.Number, title = r.Title, excerpt = r.Excerpt, body_md = r.BodyMd, html = Markdown.ToHtml(r.BodyMd),
                by = r.Actor, by_label = r.ActorLabel, at = r.CreatedAt,
            });
        });

        // The blog's history, newest first: every event, with who did it in words and the post it touched.
        api.MapGet("/blogs/{blogId:guid}/history", async (HttpContext ctx, Guid blogId, long? before, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var rows = await db.ReadAsync(tx => tx.Query<HistoryRow>("""
                SELECT e.id, e.kind, e.actor, actor_label(e.actor) AS actor_label, e.target_type, e.target_id,
                       CASE WHEN e.target_type = 'post' THEN (SELECT r.title FROM post p JOIN post_revision r ON r.id = p.current_revision_id WHERE p.id = e.target_id)
                            WHEN e.target_type = 'approval_request' THEN (SELECT r.title FROM approval_request q JOIN post p ON p.id = q.post_id
                                                                          JOIN post_revision r ON r.id = p.current_revision_id WHERE q.id = e.target_id)
                            WHEN e.target_type = 'connection' THEN (SELECT label FROM connection WHERE id = e.target_id)
                       END AS subject,
                       CASE WHEN e.target_type = 'post' THEN e.target_id
                            WHEN e.target_type = 'approval_request' THEN (SELECT post_id FROM approval_request WHERE id = e.target_id)
                       END AS post_id,
                       e.payload::text AS payload, e.at
                FROM event e
                WHERE e.blog_id = @blogId AND (@before::bigint IS NULL OR e.id < @before)
                ORDER BY e.id DESC
                LIMIT 50
                """, new { blogId, before }), ctx.RequestAborted);
            return Results.Json(new
            {
                events = rows.Select(e => new
                {
                    id = e.Id, kind = e.Kind, actor = e.Actor, actor_label = e.ActorLabel, target_type = e.TargetType,
                    subject = e.Subject, post = e.PostId, payload = System.Text.Json.JsonDocument.Parse(e.Payload).RootElement, at = e.At,
                }),
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

        api.MapPost("/posts/{id:guid}/suggestions/{decision:regex(^(accept|dismiss)$)}",
            async (HttpContext ctx, Guid id, string decision, SuggestionBody body, PostService posts, Db db) =>
            {
                if (await BlogOfAsync(db, ctx, id) is not { } blogId) return NotFound();
                if (!Taxonomy.IsKind(body.Kind) || string.IsNullOrWhiteSpace(body.Name))
                    return SignIn.Problem(400, "invalid_argument", "The body is { \"kind\": \"category\" | \"tag\", \"name\" }.");
                return Result(await posts.DecideSuggestionAsync(Actor.Owner(blogId, SignIn.Current(ctx).AccountId), id.ToString(),
                    body.Kind!, body.Name!, decision == "accept", ctx.RequestAborted));
            });

        api.MapGet("/blogs/{blogId:guid}/terms", async (HttpContext ctx, Guid blogId, Taxonomy terms, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var term = (TermView t) => new { id = t.Id, kind = t.Kind, slug = t.Slug, name = t.Name, description = t.Description, live_posts = t.LivePosts, posts = t.Posts };
            return Results.Json(new
            {
                categories = (await terms.ListAsync(blogId, Taxonomy.Category, ctx.RequestAborted)).Select(term),
                tags = (await terms.ListAsync(blogId, Taxonomy.Tag, ctx.RequestAborted)).Select(term),
            });
        });

        api.MapGet("/requests", async (HttpContext ctx, PostService posts) =>
            Results.Json(new { requests = (await posts.WaitingForAsync(SignIn.Current(ctx).AccountId, ctx.RequestAborted)).Select(RequestJson) }));

        api.MapPost("/requests/{id:guid}/{decision:regex(^(approve|decline|changes)$)}", async (HttpContext ctx, Guid id, string decision, PostService posts) =>
        {
            // The body is optional ({ "note" }): read here so a bare POST still matches (see the transition route).
            DecisionBody? body = null;
            if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
            {
                try { body = await ctx.Request.ReadFromJsonAsync<DecisionBody>(ctx.RequestAborted); }
                catch (System.Text.Json.JsonException) { return SignIn.Problem(400, "invalid_argument", "The body is JSON: { \"note\" }, optional (required to ask for changes)."); }
            }
            return Result(await posts.DecideAsync(SignIn.Current(ctx).AccountId, id, decision, body?.Note, ctx.RequestAborted));
        });
    }

    public sealed record DecisionBody([property: JsonPropertyName("note")] string? Note);

    public sealed record SuggestionBody(
        [property: JsonPropertyName("kind")] string? Kind,
        [property: JsonPropertyName("name")] string? Name);

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
        terms = p.Terms.Select(TermJson), suggested = p.Suggested.Select(x => new { kind = x.Kind, name = x.Name }),
    };

    private static object TermJson(TermRef t) => new { kind = t.Kind, slug = t.Slug, name = t.Name };

    internal static object RequestJson(RequestView q)
    {
        using var payload = System.Text.Json.JsonDocument.Parse(q.Payload);
        var p = payload.RootElement;
        string? Str(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        return new
        {
            id = q.Id, blog = q.BlogId, post = q.PostId, post_title = q.PostTitle, action = q.Action, pinned_revision = q.PinnedRevisionId,
            state = q.State, agent = q.ConnectionLabel, created_at = q.CreatedAt, decided_at = q.DecidedAt, note = q.DecisionNote,
            // Whether approving would still apply: the post is at the revision the agent asked about.
            still_applies = PostService.WhyNotApplies(q, DateTimeOffset.UtcNow) is null,
            why_not = PostService.WhyNotApplies(q, DateTimeOffset.UtcNow),
            // Exactly what is asked: an edit's new words (as readers would see them), a schedule's time.
            proposal = q.Action == "edit"
                ? new
                {
                    title = Str("title"), excerpt = Str("excerpt"), body_md = Str("body_md"), html = (string?)Markdown.ToHtml(Str("body_md") ?? ""), at = (string?)null,
                    terms = (object?)Taxonomy.Parse(q.ProposedTermsJson).Select(TermJson),
                    suggested = (object?)(p.TryGetProperty("suggested_terms", out var sg) ? Taxonomy.ParseSuggested(sg.GetRawText()).Select(x => new { kind = x.Kind, name = x.Name }) : null),
                }
                : q.Action == "schedule"
                    ? new { title = (string?)null, excerpt = (string?)null, body_md = (string?)null, html = (string?)null, at = Str("at"), terms = (object?)null, suggested = (object?)null }
                    : null,
            // What the post says now, so the app can show exactly what an edit changes.
            current = q.Action == "edit" ? new { title = q.PostTitle, excerpt = q.CurrentExcerpt, body_md = q.CurrentBodyMd, terms = Taxonomy.Parse(q.CurrentTermsJson).Select(TermJson) } : null,
        };
    }

    private sealed record HistoryRow(long Id, string Kind, string Actor, string ActorLabel, string TargetType, Guid TargetId,
        string? Subject, Guid? PostId, string Payload, DateTime At);

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
