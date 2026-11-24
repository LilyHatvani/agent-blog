using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBlog.Auth;
using AgentBlog.Database;
using AgentBlog.Public;

namespace AgentBlog.Blogs;

/// <summary>
/// The comments slot (docs/specs/embeds.md): the owner names a comments widget's page, as an https address template with
/// <c>{post}</c> in its path, and every post page frames that page for the post, in a sandboxed iframe. The frame is
/// cross-origin, so the widget runs on its own site and can't reach this blog's pages, cookies or API: it sees the post's
/// uuid and what its own users do there. The page's CSP widens <c>frame-src</c> to exactly the widget's origin, and
/// <c>script-src</c> to exactly the blog's own resize script (<c>/assets/embed-resize.js</c>), which takes a height
/// from that frame and nothing else. Without the setting the page and its CSP are what they were before the slot existed.
/// Owner only: no agent tool and no approval request reaches it.
/// </summary>
public static class Embeds
{
    /// <summary>The key in <c>blog.settings</c>.</summary>
    public const string SettingKey = "comments_embed";
    public const string Placeholder = "{post}";
    public const int MaxTemplate = 500;
    public const string ResizeScriptPath = "/assets/embed-resize.js";

    /// <summary>What the frame may do inside its own origin: run its script, keep its own cookies (it's cross-origin, so
    /// "same origin" is the widget's site, never this blog's), open its own sign-in popup, and post its forms.</summary>
    public const string Sandbox = "allow-scripts allow-same-origin allow-popups allow-forms";

    /// <summary>A checked slot: the address template and the origin it frames.</summary>
    public sealed record Comments(string UrlTemplate, string FrameOrigin)
    {
        /// <summary>The frame's address for one post: the template with the uuid, percent-encoded, in place of {post}.</summary>
        public string UrlFor(Guid post) => UrlTemplate.Replace(Placeholder, Uri.EscapeDataString(post.ToString("D")), StringComparison.Ordinal);
    }

    public sealed record Body([property: JsonPropertyName("url_template")] string? UrlTemplate);

    private sealed record Stored([property: JsonPropertyName("url_template")] string? UrlTemplate);

    /// <summary>
    /// The widget page's address template: one absolute https address with a host, <c>{post}</c> exactly once and in the
    /// path, no credentials, query, fragment, wildcard, quotes, markup or whitespace, at most 500 characters. The origin
    /// that goes into the CSP is written from the parsed address, never the text given.
    /// </summary>
    public static (Comments? Comments, string? Error) Clean(string? given)
    {
        var text = given?.Trim() ?? "";
        if (text.Length is 0 or > MaxTemplate || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\'' or '"' or '<' or '>' or '*' or ';' or ','))
            return (null, $"The widget's address is one https address of at most {MaxTemplate} characters, like https://comments.example.com/posts/{{post}}.");
        if (CountOf(text, Placeholder) != 1)
            return (null, "The widget's address carries {post} exactly once, where the post's id goes: https://comments.example.com/posts/{post}.");
        // Parse it with a stand-in for {post}, then check {post} landed in the path.
        const string standIn = "agentblog0post0stand0in";
        if (!Uri.TryCreate(text.Replace(Placeholder, standIn, StringComparison.Ordinal), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return (null, "The widget's address must be https: a page served over https can only frame https.");
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.HostNameType == UriHostNameType.Unknown)
            return (null, "The widget's address can't carry credentials, a ?query or a #fragment.");
        if (!uri.AbsolutePath.Contains(standIn, StringComparison.Ordinal))
            return (null, "{post} goes in the address's path: https://comments.example.com/posts/{post}.");
        return (new Comments(uri.AbsoluteUri.Replace(standIn, Placeholder, StringComparison.Ordinal), Origin(uri)), null);
    }

    private static int CountOf(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;

    /// <summary>
    /// The post page's CSP: the site's own, plus <c>script-src</c> for exactly the resize script's address on this blog,
    /// and <c>frame-src</c> for exactly the widget's origin, when the slot is set. Unset, it is <paramref name="basePolicy"/>.
    /// </summary>
    public static string Csp(string basePolicy, Comments? comments, string blogUrl)
    {
        if (comments is null) return basePolicy;
        var added = $"script-src {blogUrl.TrimEnd('/')}{ResizeScriptPath}; frame-src {comments.FrameOrigin}";
        // Before base-uri, so the page's own directives read in the same order as before.
        var at = basePolicy.IndexOf("; base-uri", StringComparison.Ordinal);
        return at < 0 ? basePolicy + "; " + added : basePolicy[..at] + "; " + added + basePolicy[at..];
    }

    /// <summary>
    /// The blog's slot, or null when unset. Read back through <see cref="Clean"/>: a stored value that no longer passes
    /// (written by hand, or by the script-embed shape this replaced) puts nothing on the page.
    /// </summary>
    public static async Task<Comments?> ForBlogAsync(Db db, Guid blogId, CancellationToken ct)
    {
        var json = await db.ReadAsync(tx => tx.One<string?>($"SELECT settings->'{SettingKey}' #>> '{{}}' FROM blog WHERE id = @blogId", new { blogId }), ct);
        if (json is null) return null;
        Stored? stored;
        try { stored = JsonSerializer.Deserialize<Stored>(json); }
        catch (JsonException) { return null; }
        return stored is null ? null : Clean(stored.UrlTemplate).Comments;
    }

    public static void MapEmbedsApi(this WebApplication app)
    {
        // The resize script: the blog's own, the same bytes for every blog. The only script a post page with the slot runs.
        app.MapGet(ResizeScriptPath, (HttpContext ctx, Theme theme) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=3600";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Text(theme.EmbedResizeJs, "text/javascript; charset=utf-8");
        });

        var api = app.MapOwnerApi();

        api.MapGet("/blogs/{blogId:guid}/embeds/comments", async (HttpContext ctx, Guid blogId, Db db) =>
            !await OwnsAsync(db, ctx, blogId) ? NotFound() : Results.Json(Json(await ForBlogAsync(db, blogId, ctx.RequestAborted))));

        api.MapPut("/blogs/{blogId:guid}/embeds/comments", async (HttpContext ctx, Guid blogId, Body body, Db db, TimeProvider clock) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var (comments, error) = Clean(body.UrlTemplate);
            if (comments is null) return SignIn.Problem(400, "invalid_argument", error!);
            var before = await ForBlogAsync(db, blogId, ctx.RequestAborted);
            await SaveAsync(db, clock, ctx, blogId, before, comments);
            return Results.Json(Json(comments));
        });

        api.MapDelete("/blogs/{blogId:guid}/embeds/comments", async (HttpContext ctx, Guid blogId, Db db, TimeProvider clock) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var before = await ForBlogAsync(db, blogId, ctx.RequestAborted);
            if (before is not null) await SaveAsync(db, clock, ctx, blogId, before, null);
            return Results.Json(Json(null));
        });
    }

    private static Task SaveAsync(Db db, TimeProvider clock, HttpContext ctx, Guid blogId, Comments? before, Comments? after) =>
        db.WriteAsync(async tx =>
        {
            var stored = after is null ? null : JsonSerializer.Serialize(new Stored(after.UrlTemplate));
            await tx.Exec(stored is null
                ? $"UPDATE blog SET settings = settings - '{SettingKey}' WHERE id = @blogId"
                : $"UPDATE blog SET settings = jsonb_set(settings, '{{{SettingKey}}}', @stored::jsonb) WHERE id = @blogId",
                new { blogId, stored });
            await tx.Event(new EventRow(blogId, "blog", blogId, "blog_settings_changed", Actors.Account(SignIn.Current(ctx).AccountId),
                new Dictionary<string, object?> { [SettingKey] = new { from = before?.UrlTemplate, to = after?.UrlTemplate } }), clock.GetUtcNow());
            return 0;
        }, ctx.RequestAborted);

    private static object Json(Comments? c) => new
    {
        set = c is not null,
        url_template = c?.UrlTemplate,
        frame_origin = c?.FrameOrigin,
    };

    private static string Origin(Uri uri) => $"https://{uri.IdnHost}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}";

    private static IResult NotFound() => SignIn.Problem(404, "not_found", "No such blog of yours.");

    private static Task<bool> OwnsAsync(Db db, HttpContext ctx, Guid blogId) => db.ReadAsync(tx => tx.One<bool>("""
        SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @blogId AND account_id = @accountId AND role = 'owner')
        """, new { blogId, accountId = SignIn.Current(ctx).AccountId }), ctx.RequestAborted);
}
