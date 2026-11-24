using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBlog.Auth;
using AgentBlog.Database;

namespace AgentBlog.Blogs;

/// <summary>
/// The comments slot (docs/specs/embeds.md): the owner names a comments widget served from another site, and every post
/// page carries an empty <c>&lt;div data-embed="comments" data-post="{post uuid}"&gt;</c> plus a <c>&lt;script src&gt;</c>
/// from that site. The page's CSP then allows script from exactly that origin and connections to exactly the origins
/// the owner listed, nothing else. Without the setting the page and its CSP are what they were before the slot existed.
/// Owner only: no agent tool and no approval request reaches it, since it puts someone else's script on every post.
/// </summary>
public static class Embeds
{
    /// <summary>The key in <c>blog.settings</c>.</summary>
    public const string SettingKey = "comments_embed";
    public const int MaxConnectOrigins = 10;
    public const int MaxScriptUrl = 500;

    /// <summary>A checked slot: the script's address, its origin (the CSP's script-src), and the origins it may call.</summary>
    public sealed record Comments(string ScriptUrl, string ScriptOrigin, IReadOnlyList<string> ConnectOrigins);

    public sealed record Body(
        [property: JsonPropertyName("script_url")] string? ScriptUrl,
        [property: JsonPropertyName("connect_origins")] IReadOnlyList<string>? ConnectOrigins);

    private sealed record Stored(
        [property: JsonPropertyName("script_url")] string? ScriptUrl,
        [property: JsonPropertyName("connect_origins")] List<string>? ConnectOrigins);

    /// <summary>
    /// An https origin and nothing else: <c>https://host</c> or <c>https://host:port</c>, an optional trailing slash, no
    /// path, query, fragment, credentials or wildcard. Returned as the parsed scheme, host and port, never the text given,
    /// so nothing typed here can add a CSP directive.
    /// </summary>
    public static (string? Origin, string? Error) CleanOrigin(string? given)
    {
        var text = given?.Trim() ?? "";
        if (text.Length is 0 or > 200 || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ';' or ',' or '\'' or '"' or '*'))
            return (null, $"'{Short(text)}' isn't an origin: give one https address, like https://comments.example.com.");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return (null, $"'{Short(text)}' isn't an https origin: only https:// addresses can serve or answer the widget.");
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/" || uri.HostNameType == UriHostNameType.Unknown)
            return (null, $"'{Short(text)}' isn't an origin: just the scheme and host (and a port), no path, query or credentials.");
        return (Origin(uri), null);
    }

    /// <summary>The widget's script: an https address with a host, no credentials or fragment. Its origin goes into the CSP.</summary>
    public static (string? Url, string? Origin, string? Error) CleanScriptUrl(string? given)
    {
        var text = given?.Trim() ?? "";
        if (text.Length is 0 or > MaxScriptUrl || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\'' or '"' or '<' or '>'))
            return (null, null, $"The script's address is one https address of at most {MaxScriptUrl} characters.");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return (null, null, "The script's address must be https: a page served over https can't load a widget from anywhere else.");
        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.HostNameType == UriHostNameType.Unknown)
            return (null, null, "The script's address can't carry credentials or a fragment.");
        return (uri.AbsoluteUri, Origin(uri), null);
    }

    /// <summary>Checks a whole slot as the owner gave it. Every origin is cleaned; duplicates are dropped.</summary>
    public static (Comments? Comments, string? Error) Clean(string? scriptUrl, IReadOnlyList<string>? connectOrigins)
    {
        var (url, origin, error) = CleanScriptUrl(scriptUrl);
        if (error is not null) return (null, error);
        var connects = new List<string>();
        foreach (var given in connectOrigins ?? [])
        {
            var (o, why) = CleanOrigin(given);
            if (why is not null) return (null, why);
            if (!connects.Contains(o!, StringComparer.Ordinal)) connects.Add(o!);
        }
        if (connects.Count > MaxConnectOrigins) return (null, $"At most {MaxConnectOrigins} origins the widget may call.");
        return (new Comments(url!, origin!, connects), null);
    }

    /// <summary>
    /// The post page's CSP: the site's own, plus <c>script-src</c> for the widget's origin and <c>connect-src</c> for the
    /// listed origins when the slot is set. Unset, it is <paramref name="basePolicy"/> unchanged.
    /// </summary>
    public static string Csp(string basePolicy, Comments? comments)
    {
        if (comments is null) return basePolicy;
        var added = "script-src " + comments.ScriptOrigin + (comments.ConnectOrigins.Count > 0 ? "; connect-src " + string.Join(' ', comments.ConnectOrigins) : "");
        // Before base-uri, so the page's own directives read in the same order as before.
        var at = basePolicy.IndexOf("; base-uri", StringComparison.Ordinal);
        return at < 0 ? basePolicy + "; " + added : basePolicy[..at] + "; " + added + basePolicy[at..];
    }

    /// <summary>
    /// The blog's slot, or null when unset. Read back through <see cref="Clean"/>: a stored value that no longer passes
    /// (written by hand, or by an older rule) puts nothing on the page.
    /// </summary>
    public static async Task<Comments?> ForBlogAsync(Db db, Guid blogId, CancellationToken ct)
    {
        var json = await db.ReadAsync(tx => tx.One<string?>($"SELECT settings->'{SettingKey}' #>> '{{}}' FROM blog WHERE id = @blogId", new { blogId }), ct);
        if (json is null) return null;
        Stored? stored;
        try { stored = JsonSerializer.Deserialize<Stored>(json); }
        catch (JsonException) { return null; }
        return stored is null ? null : Clean(stored.ScriptUrl, stored.ConnectOrigins).Comments;
    }

    public static void MapEmbedsApi(this WebApplication app)
    {
        var api = app.MapOwnerApi();

        api.MapGet("/blogs/{blogId:guid}/embeds/comments", async (HttpContext ctx, Guid blogId, Db db) =>
            !await OwnsAsync(db, ctx, blogId) ? NotFound() : Results.Json(Json(await ForBlogAsync(db, blogId, ctx.RequestAborted))));

        // The whole slot at once: one form, one Save.
        api.MapPut("/blogs/{blogId:guid}/embeds/comments", async (HttpContext ctx, Guid blogId, Body body, Db db, TimeProvider clock) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var (comments, error) = Clean(body.ScriptUrl, body.ConnectOrigins);
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
            var stored = after is null ? null : JsonSerializer.Serialize(new Stored(after.ScriptUrl, [.. after.ConnectOrigins]));
            await tx.Exec(stored is null
                ? $"UPDATE blog SET settings = settings - '{SettingKey}' WHERE id = @blogId"
                : $"UPDATE blog SET settings = jsonb_set(settings, '{{{SettingKey}}}', @stored::jsonb) WHERE id = @blogId",
                new { blogId, stored });
            await tx.Event(new EventRow(blogId, "blog", blogId, "blog_settings_changed", Actors.Account(SignIn.Current(ctx).AccountId),
                new Dictionary<string, object?> { [SettingKey] = new { from = Describe(before), to = Describe(after) } }), clock.GetUtcNow());
            return 0;
        }, ctx.RequestAborted);

    private static object? Describe(Comments? c) => c is null ? null : new { script_url = c.ScriptUrl, connect_origins = c.ConnectOrigins };

    private static object Json(Comments? c) => new
    {
        set = c is not null,
        script_url = c?.ScriptUrl,
        script_origin = c?.ScriptOrigin,
        connect_origins = c?.ConnectOrigins ?? [],
        max_connect_origins = MaxConnectOrigins,
    };

    private static string Origin(Uri uri) => $"https://{uri.IdnHost}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}";

    private static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s;

    private static IResult NotFound() => SignIn.Problem(404, "not_found", "No such blog of yours.");

    private static Task<bool> OwnsAsync(Db db, HttpContext ctx, Guid blogId) => db.ReadAsync(tx => tx.One<bool>("""
        SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @blogId AND account_id = @accountId AND role = 'owner')
        """, new { blogId, accountId = SignIn.Current(ctx).AccountId }), ctx.RequestAborted);
}
