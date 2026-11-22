using AgentKit.Secrets;
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

        // Cloudflare in front of the pictures (decision 0009): connect a zone with a purge-only token, or disconnect.
        api.MapGet("/blogs/{blogId:guid}/cdn", async (HttpContext ctx, Guid blogId, CdnLinks cdn, Db db) =>
            await OwnsAsync(db, ctx, blogId) ? Results.Json(CdnJson(await cdn.StateAsync(blogId, ctx.RequestAborted)))
                : SignIn.Problem(404, "not_found", "No such blog of yours."));

        api.MapPut("/blogs/{blogId:guid}/cdn", async (HttpContext ctx, Guid blogId, CdnBody body, CdnLinks cdn, Db db, Sealer sealer) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return SignIn.Problem(404, "not_found", "No such blog of yours.");
            var (state, error) = await cdn.ConnectAsync(Actor.Owner(blogId, SignIn.Current(ctx).AccountId), body.ZoneId, body.Token, body.EdgeSeconds, ctx.RequestAborted);
            return state is not null ? Results.Json(CdnJson(state))
                : SignIn.Problem(sealer.On ? 400 : 409, sealer.On ? "invalid_argument" : "secrets_off", error!);
        });

        api.MapDelete("/blogs/{blogId:guid}/cdn", async (HttpContext ctx, Guid blogId, CdnLinks cdn, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return SignIn.Problem(404, "not_found", "No such blog of yours.");
            var (done, note) = await cdn.DisconnectAsync(Actor.Owner(blogId, SignIn.Current(ctx).AccountId), ctx.RequestAborted);
            return done ? Results.Json(new { disconnected = true, note }) : SignIn.Problem(404, "not_found", "Cloudflare isn't connected.");
        });

        app.MapMethods("/media/{id:guid}/{filename}", ["GET", "HEAD"], async (HttpContext ctx, Guid id, string filename, MediaService media,
            CdnLinks cdn, Db db, IConfiguration config) =>
        {
            var h = ctx.Response.Headers;
            // A picture that isn't served isn't cached either: restoring one shows it again at once.
            h.CacheControl = "no-store";
            if (await PublicSite.ResolveAsync(ctx, db, config) is not { } blog) return Results.NotFound();
            // One address per picture: the right id with another name is not it.
            if (await media.ReadyAsync(blog.Id, id, ctx.RequestAborted) is not { } m || m.Filename != filename) return Results.NotFound();

            var etag = $"\"{Convert.ToHexStringLower(m.Sha256)}\"";
            h.ETag = etag;
            // An hour: long enough for a page's readers, short enough that a picture the owner trashed stops being served soon.
            h.CacheControl = "public, max-age=3600";
            // Cloudflare (decision 0009) reads and strips these. The tags let trash, and disconnecting, purge the edge;
            // connected, the edge keeps a copy longer than browsers do, because trash purges it at once.
            h["Cache-Tag"] = $"{CdnLinks.Tag(m.Id)},{CdnLinks.BlogTag(blog.Id)}";
            if (await cdn.EdgeSecondsAsync(blog.Id, ctx.RequestAborted) is { } edge)
                h["Cloudflare-CDN-Cache-Control"] = $"max-age={edge}";
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

    public sealed record CdnBody(
        [property: System.Text.Json.Serialization.JsonPropertyName("zone_id")] string? ZoneId,
        [property: System.Text.Json.Serialization.JsonPropertyName("token")] string? Token,
        [property: System.Text.Json.Serialization.JsonPropertyName("edge_seconds")] int? EdgeSeconds);

    private static object CdnJson(CdnState c) => new
    {
        connected = c.Connected, provider = c.Connected ? "cloudflare" : null, zone_id = c.ZoneId, edge_seconds = c.EdgeSeconds,
        connected_at = c.ConnectedAt, last_purge_at = c.LastPurgeAt, failing_since = c.FailingSince, last_error = c.LastError,
        refused = c.Refused, waiting = c.Waiting,
        default_edge_seconds = CdnLinks.DefaultEdgeSeconds, min_edge_seconds = CdnLinks.MinEdgeSeconds, max_edge_seconds = CdnLinks.MaxEdgeSeconds,
    };

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
