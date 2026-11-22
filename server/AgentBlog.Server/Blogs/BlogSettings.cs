using System.Text.Json.Serialization;
using AgentBlog.Auth;
using AgentBlog.Database;
using AgentBlog.Public;

namespace AgentBlog.Blogs;

/// <summary>
/// The blog's own settings, the owner's (docs/specs/taxonomy.md, themes.md): its title and description, which readers see
/// in the header, the feeds and link previews; its theme; and whether readers may try the other themes on the site. An agent's <c>edit_blog_settings</c> (Full, an approval request below) comes
/// with M2, when approval requests cover more than posts.
/// </summary>
public static class BlogSettings
{
    public const int MaxTitle = 200;
    public const int MaxDescription = 1000;

    public sealed record Patch(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("theme")] string? Theme,
        [property: JsonPropertyName("theme_picker")] bool? ThemePicker,
        [property: JsonPropertyName("search_ping")] bool? SearchPing,
        [property: JsonPropertyName("bylines")] bool? Bylines = null,
        [property: JsonPropertyName("author_name")] string? AuthorName = null);

    private sealed record Row(Guid Id, string Slug, string Title, string Description, string Theme, bool ThemePicker, bool SearchPing,
        bool Bylines, string AuthorName, string OwnerName);

    private const string Columns = "b.id, b.slug, b.title, b.description, coalesce(b.settings->>'theme', 'paper') AS theme, coalesce((b.settings->>'theme_picker')::boolean, false) AS theme_picker, coalesce((b.settings->>'search_ping')::boolean, true) AS search_ping, coalesce((b.settings->>'bylines')::boolean, true) AS bylines, coalesce(b.settings->>'author_name', '') AS author_name, (SELECT a.display_name FROM blog_member om JOIN account a ON a.id = om.account_id WHERE om.blog_id = b.id AND om.role = 'owner' ORDER BY om.created_at LIMIT 1) AS owner_name";

    public static void MapBlogSettingsApi(this WebApplication app)
    {
        var api = app.MapOwnerApi();

        api.MapGet("/blogs/{blogId:guid}", async (HttpContext ctx, Guid blogId, Db db, Theme themes, SearchPing ping) =>
            await OwnedAsync(db, ctx, blogId) is { } b ? Results.Json(Json(b, themes, await ping.StateAsync(blogId, ctx.RequestAborted))) : NotFound());

        // Surgical: a field left out stays as it is; "" clears the description (a title can't be empty).
        api.MapMethods("/blogs/{blogId:guid}", ["PATCH"], async (HttpContext ctx, Guid blogId, Patch body, Db db, TimeProvider clock, Theme themes, SearchPing ping) =>
        {
            if (await OwnedAsync(db, ctx, blogId) is not { } before) return NotFound();
            var title = body.Title?.Trim();
            var description = body.Description?.Trim();
            if (title is not null && title.Length is < 1 or > MaxTitle)
                return SignIn.Problem(400, "invalid_argument", $"The title is 1–{MaxTitle} characters.");
            if (description is not null && description.Length > MaxDescription)
                return SignIn.Problem(400, "invalid_argument", $"The description is at most {MaxDescription} characters.");

            var authorName = body.AuthorName is null ? null : string.Join(' ', body.AuthorName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (authorName is { Length: > 60 } || (body.AuthorName?.Any(char.IsControl) ?? false))
                return SignIn.Problem(400, "invalid_argument", "Your name on posts is at most 60 characters, on one line.");

            if (body.Theme is not null && !themes.Exists(body.Theme))
                return SignIn.Problem(400, "invalid_argument", $"theme is one of: {string.Join(", ", themes.All.Select(t => t.Name))}.");

            var changed = new Dictionary<string, object>();
            if (body.Theme is not null && body.Theme != before.Theme) changed["theme"] = new { from = before.Theme, to = body.Theme };
            if (body.ThemePicker is { } picker && picker != before.ThemePicker) changed["theme_picker"] = new { from = before.ThemePicker, to = picker };
            if (body.SearchPing is { } searchPing && searchPing != before.SearchPing) changed["search_ping"] = new { from = before.SearchPing, to = searchPing };
            if (body.Bylines is { } bylines && bylines != before.Bylines) changed["bylines"] = new { from = before.Bylines, to = bylines };
            if (authorName is not null && authorName != before.AuthorName) changed["author_name"] = new { from = before.AuthorName, to = authorName };
            if (title is not null && title != before.Title) changed["title"] = new { from = before.Title, to = title };
            if (description is not null && description != before.Description) changed["description"] = new { from = before.Description, to = description };
            if (changed.Count == 0) return Results.Json(Json(before, themes, await ping.StateAsync(blogId, ctx.RequestAborted)));

            var owner = Actors.Account(SignIn.Current(ctx).AccountId);
            var after = await db.WriteAsync(async tx =>
            {
                var now = clock.GetUtcNow();
                var settings = new Dictionary<string, object>();
                if (body.Theme is not null) settings["theme"] = body.Theme;
                if (body.ThemePicker is { } p) settings["theme_picker"] = p;
                if (body.SearchPing is { } sp) settings["search_ping"] = sp;
                if (body.Bylines is { } by) settings["bylines"] = by;
                if (authorName is not null) settings["author_name"] = authorName; // "" goes back to the account's name
                await tx.Exec("""
                    UPDATE blog SET title = coalesce(@title, title), description = coalesce(@description, description),
                                    settings = settings || @settings::jsonb
                    WHERE id = @blogId
                    """, new { title, description, blogId, settings = System.Text.Json.JsonSerializer.Serialize(settings) });
                await tx.Event(new EventRow(blogId, "blog", blogId, "blog_settings_changed", owner, changed), now);
                return (await tx.One<Row>($"SELECT {Columns} FROM blog b WHERE b.id = @blogId", new { blogId }))!;
            }, ctx.RequestAborted);
            return Results.Json(Json(after, themes, await ping.StateAsync(blogId, ctx.RequestAborted)));
        });
    }

    private static object Json(Row b, Theme themes, IReadOnlyList<SearchPing.PingState> pings) => new
    {
        search_ping = b.SearchPing,
        bylines = b.Bylines,
        author_name = b.AuthorName,
        author_name_default = b.OwnerName,
        pings = pings.Select(p => new { service = p.Service, at = p.LastAt, status = p.LastStatus, error = p.LastError, urls = p.LastUrls }),
        id = b.Id, slug = b.Slug, title = b.Title, description = b.Description,
        title_max = MaxTitle, description_max = MaxDescription,
        theme = themes.Exists(b.Theme) ? b.Theme : Theme.Default, theme_picker = b.ThemePicker,
        themes = themes.All.Select(t => new { name = t.Name, label = t.Label, description = t.Description }),
    };

    private static IResult NotFound() => SignIn.Problem(404, "not_found", "No such blog of yours.");

    private static Task<Row?> OwnedAsync(Db db, HttpContext ctx, Guid blogId) => db.ReadAsync(tx => tx.One<Row>($"""
        SELECT {Columns} FROM blog b
        JOIN blog_member m ON m.blog_id = b.id AND m.account_id = @accountId AND m.role = 'owner'
        WHERE b.id = @blogId
        """, new { blogId, accountId = SignIn.Current(ctx).AccountId }), ctx.RequestAborted);
}
