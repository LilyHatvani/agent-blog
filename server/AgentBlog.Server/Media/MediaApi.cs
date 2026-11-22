using AgentBlog.Auth;
using AgentBlog.Database;
using AgentBlog.Posts;
using AgentBlog.Public;

namespace AgentBlog.Media;

/// <summary>
/// The owner's media API (the library in the app: list, trash, restore) and the readers' address,
/// <c>/media/{id}/{filename}</c>, served from the bucket through the blog's own origin (docs/specs/media.md § Serving).
/// </summary>
public static class MediaApi
{
    public static void MapMediaApi(this WebApplication app)
    {
        var api = app.MapOwnerApi();

        api.MapGet("/blogs/{blogId:guid}/media", async (HttpContext ctx, Guid blogId, string? status, int? offset, MediaService media, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return SignIn.Problem(404, "not_found", "No such blog of yours.");
            if (status is not (null or "ready" or "trash")) return SignIn.Problem(400, "invalid_argument", "status is ready or trash.");
            var list = await media.ListAsync(blogId, status, 100, offset ?? 0, ctx.RequestAborted);
            return Results.Json(new { enabled = media.Enabled, media = list.Select(Json) });
        });

        api.MapPost("/media/{id:guid}/{op:regex(^(trash|restore)$)}", async (HttpContext ctx, Guid id, string op, MediaService media) =>
        {
            var account = SignIn.Current(ctx).AccountId;
            if (await media.OwnedBlogOfAsync(account, id, ctx.RequestAborted) is not { } blogId)
                return SignIn.Problem(404, "not_found", "No such media on your blogs.");
            var owner = Actor.Owner(blogId, account);
            var o = op == "trash" ? await media.TrashAsync(owner, id, ctx.RequestAborted) : await media.RestoreAsync(owner, id, ctx.RequestAborted);
            return o.Ok ? Results.Json(new { media = Json(o.Media!) })
                : SignIn.Problem(o.Code == "not_found" ? 404 : o.Code == "invalid_state" ? 409 : 400, o.Code!, o.Message!);
        });

        app.MapMethods("/media/{id:guid}/{filename}", ["GET", "HEAD"], async (HttpContext ctx, Guid id, string filename, MediaService media,
            Db db, IConfiguration config) =>
        {
            if (await PublicSite.ResolveAsync(ctx, db, config) is not { } blog) return Results.NotFound();
            // One address per picture: the right id with another name is not it.
            if (await media.ReadyAsync(blog.Id, id, ctx.RequestAborted) is not { } m || m.Filename != filename) return Results.NotFound();

            var h = ctx.Response.Headers;
            var etag = $"\"{Convert.ToHexStringLower(m.Sha256)}\"";
            h.ETag = etag;
            // An hour: long enough for a page's readers, short enough that a picture the owner trashed stops being served soon.
            h.CacheControl = "public, max-age=3600";
            h["X-Content-Type-Options"] = "nosniff";
            h.ContentSecurityPolicy = "default-src 'none'; sandbox";
            h.ContentDisposition = $"inline; filename=\"{m.Filename}\"";
            if (ctx.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(304);
            if (HttpMethods.IsHead(ctx.Request.Method))
            {
                ctx.Response.ContentType = m.ContentType;
                ctx.Response.ContentLength = m.SizeBytes;
                return Results.Empty;
            }
            if (await media.OpenAsync(m, ctx.RequestAborted) is not { } body) return Results.NotFound();
            ctx.Response.RegisterForDisposeAsync(body);
            ctx.Response.ContentLength = m.SizeBytes;
            return Results.Stream(body.Body, m.ContentType);
        });
    }

    internal static object Json(MediaView m) => new
    {
        id = m.Id, status = m.Status, path = m.Path, markdown = m.Markdown, filename = m.Filename, alt = m.Alt,
        content_type = m.ContentType, size_bytes = m.SizeBytes, source_url = m.SourceUrl, by = m.CreatedBy, by_label = m.CreatedByLabel,
        created_at = m.CreatedAt, ready_at = m.ReadyAt, trashed_at = m.TrashedAt,
    };

    private static Task<bool> OwnsAsync(Db db, HttpContext ctx, Guid blogId) => db.ReadAsync(tx => tx.One<bool>("""
        SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @blogId AND account_id = @accountId AND role = 'owner')
        """, new { blogId, accountId = SignIn.Current(ctx).AccountId }), ctx.RequestAborted);
}
