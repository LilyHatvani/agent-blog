using AgentBlog.Auth;
using AgentBlog.Posts;
using Microsoft.AspNetCore.Http.Features;

namespace AgentBlog.Export;

/// <summary>
/// <c>GET /api/blogs/{id}/export</c>: the owner downloads everything as one zip. <c>POST /api/blogs/{id}/import-export</c>
/// (the zip as the body, <c>application/zip</c>): the owner brings one into a blog with nothing in it yet.
/// docs/specs/export.md.
/// </summary>
public static class BlogExportApi
{
    /// <summary>The largest export an import takes: the document plus every picture.</summary>
    public const long MaxUpload = 2L * 1024 * 1024 * 1024;

    public static void MapBlogExport(this WebApplication app)
    {
        var api = app.MapOwnerApi();

        api.MapGet("/blogs/{blogId:guid}/export", async (HttpContext ctx, Guid blogId, Members members, BlogExport export, Database.Db db) =>
        {
            if (await OwnerAsync(ctx, members, blogId) is not { } owner) return SignIn.Problem(404, "not_found", "No such blog of yours.");
            var slug = await db.ReadAsync(tx => tx.One<string>("SELECT slug FROM blog WHERE id = @blogId", new { blogId }), ctx.RequestAborted);
            // Built in a file, then sent: a zip finishes with a synchronous write, and the download gets its length.
            var path = Path.Combine(Path.GetTempPath(), $"agent-blog-export-{Guid.NewGuid():N}.zip");
            try
            {
                await using (var file = File.Create(path)) await export.WriteAsync(owner, file, ctx.RequestAborted);
                ctx.Response.ContentType = "application/zip";
                ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{slug}-{DateTime.UtcNow:yyyy-MM-dd}.agent-blog.zip\"";
                ctx.Response.Headers.CacheControl = "no-store";
                ctx.Response.ContentLength = new FileInfo(path).Length;
                await using (var file = File.OpenRead(path)) await file.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            }
            finally
            {
                File.Delete(path);
            }
            return Results.Empty;
        });

        api.MapPost("/blogs/{blogId:guid}/import-export", async (HttpContext ctx, Guid blogId, Members members, BlogExport export) =>
        {
            if (await OwnerAsync(ctx, members, blogId) is not { } owner) return SignIn.Problem(404, "not_found", "No such blog of yours.");
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxUpload;
            var path = Path.Combine(Path.GetTempPath(), $"agent-blog-import-{Guid.NewGuid():N}.zip");
            try
            {
                await using (var file = File.Create(path)) await ctx.Request.Body.CopyToAsync(file, ctx.RequestAborted);
                if (new FileInfo(path).Length == 0) return SignIn.Problem(400, "invalid_argument", "Send the export's zip as the request body.");
                (BlogExport.Imported? done, string? why) result;
                try { result = await export.ImportAsync(owner, path, ctx.RequestAborted); }
                catch (InvalidDataException) { result = (null, "That isn't a zip file: choose the .agent-blog.zip an export made."); }
                return result.done is { } d
                    ? Results.Json(new { imported = d.Counts, pictures = d.Pictures, subscribers = d.Subscribers })
                    : SignIn.Problem(400, "invalid_argument", result.why!);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    private static async Task<Actor?> OwnerAsync(HttpContext ctx, Members members, Guid blogId) =>
        await members.OnBlogAsync(blogId, SignIn.Current(ctx).AccountId, ctx.RequestAborted) is { IsOwner: true } owner ? owner : null;
}
