namespace AgentBlog.Auth;

/// <summary>
/// The owner's phone app (docs/specs/app.md), a PWA built from <c>pwa/</c> into <c>wwwroot/admin</c>, served at
/// <c>/admin</c>. Its static files (scripts, styles, icons, the service worker) are public; any other path under /admin is
/// an app screen, served only to a signed-in owner and otherwise sent to sign-in and back. An unbuilt app (local tests)
/// serves a page saying so.
/// </summary>
public static class AdminApp
{
    private const string Csp = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' https: data:; " +
                               "manifest-src 'self'; worker-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".webmanifest"] = "application/manifest+json",
        [".json"] = "application/json",
        [".html"] = "text/html; charset=utf-8",
    };

    public static void MapAdminApp(this WebApplication app)
    {
        var root = app.Configuration["Blog:App:Root"] is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(AppContext.BaseDirectory, "wwwroot", "admin");

        app.MapGet("/admin", (HttpContext ctx, Sessions sessions) => ShellAsync(ctx, sessions, root));
        app.MapGet("/admin/{**path}", async (HttpContext ctx, string path, Sessions sessions) =>
        {
            if (Path.HasExtension(path))
            {
                // A file of the built app, or nothing. Never outside the app's own folder.
                var full = Path.GetFullPath(Path.Combine(root, path));
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
                    return Results.NotFound();
                var h = ctx.Response.Headers;
                h["X-Content-Type-Options"] = "nosniff";
                // Hashed bundles never change; the shell, the worker and the manifest must be fetched fresh.
                h.CacheControl = path.StartsWith("assets/", StringComparison.Ordinal) ? "public, max-age=31536000, immutable" : "no-cache";
                if (path == "sw.js") h["Service-Worker-Allowed"] = "/admin/";
                return Results.File(full, Types.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream"));
            }
            return await ShellAsync(ctx, sessions, root);
        });
    }

    private static async Task<IResult> ShellAsync(HttpContext ctx, Sessions sessions, string root)
    {
        if (await sessions.AuthenticateAsync(ctx, ctx.RequestAborted) is null)
            return Results.Redirect("/sign-in?returnTo=" + Uri.EscapeDataString("/admin"));
        var h = ctx.Response.Headers;
        h.ContentSecurityPolicy = Csp;
        h.CacheControl = "no-store";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Content-Type-Options"] = "nosniff";
        var index = Path.Combine(root, "index.html");
        return File.Exists(index)
            ? Results.File(index, "text/html; charset=utf-8")
            : Results.Content("""<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Agent Blog</title></head><body><div id="app"></div><p>The app isn't built in this copy of the server (pwa/ → npm run build).</p></body></html>""",
                "text/html; charset=utf-8");
    }
}
