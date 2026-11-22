using System.Globalization;
using System.Text;
using System.Xml;
using AgentBlog.Database;

namespace AgentBlog.Public;

/// <summary>
/// What readers see (docs/specs/public-site.md): the home page, post pages, RSS, Atom, the sitemap and robots.txt. Only
/// live posts exist here: a draft, a scheduled post or one in the trash answers 404 exactly like a slug that never
/// existed. Server-rendered, no JavaScript. Feeds are hand-written with XmlWriter (EchoOfDreams' BlogSeoEndpoints).
/// </summary>
public static class PublicSite
{
    public const int PageSize = 10;
    private const int FeedSize = 30;

    public sealed record SiteBlog(Guid Id, string Slug, string Title, string Description, string Url, string Theme, bool ThemePicker);

    /// <summary>A reader's theme preview, for this browser (docs/specs/themes.md): a session cookie, never an account setting.</summary>
    public const string PreviewCookie = "blog_theme";

    private sealed record PublicPost(string Slug, string Title, string Excerpt, string BodyMd, DateTime PublishedAt, DateTime UpdatedAt, string TermsJson);

    private sealed record PublicTerm(Guid Id, string Kind, string Slug, string Name, string Description);

    public static void MapPublicSite(this WebApplication app)
    {
        // Theme styles. /assets/theme.css is Paper's, for pages cached from before themes.
        app.MapGet("/assets/theme.css", (HttpContext ctx, Theme theme) => Css(ctx, theme.Css(Theme.Default)));
        app.MapGet("/assets/themes/_picker.css", (HttpContext ctx, Theme theme) => Css(ctx, theme.PickerCss));
        app.MapGet("/assets/themes/{name}.css", (HttpContext ctx, string name, Theme theme) =>
            theme.Exists(name) ? Css(ctx, theme.Css(name)) : Results.NotFound());

        // The themes page: every built-in theme, with links to see this blog in it. Only where readers may try themes
        // (the owner's switch), or for the owner, who can always preview.
        app.MapGet("/themes", async (HttpContext ctx, Db db, Theme theme, IConfiguration config) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return NotSetUp(ctx, config);
            if (!blog.ThemePicker && !await IsOwnerAsync(ctx, blog)) return await NotFoundAsync(ctx, theme, blog);
            var model = Model(blog, Meta(blog, $"Themes · {blog.Title}", $"See {blog.Title} in each theme.", blog.Url + "themes", "website", null, noindex: true));
            var latest = (await LiveAsync(db, blog.Id, 1, 0, ctx.RequestAborted)).FirstOrDefault();
            model["latest_post_url"] = latest is null ? null : PostUrl(blog, latest.Slug);
            return await PageAsync(ctx, theme, blog, "themes", model, 200);
        });

        app.MapGet("/", async (HttpContext ctx, Db db, Theme theme, IConfiguration config, int? page) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return NotSetUp(ctx, config);
            var number = Math.Max(page ?? 1, 1);
            var posts = await LiveAsync(db, blog.Id, PageSize + 1, (number - 1) * PageSize, ctx.RequestAborted);
            var hasOlder = posts.Count > PageSize;
            var model = Model(blog, Meta(blog, number == 1 ? blog.Title : $"{blog.Title} · page {number}",
                blog.Description, number == 1 ? blog.Url : $"{blog.Url}?page={number}", "website", null, noindex: false));
            var card = Social.BlogCard(blog.Url, CardTheme(theme, blog), BlogCardText(blog));
            Image(model, card);
            if (number == 1) ((Dictionary<string, object?>)model["meta"]!)["json_ld"] = Social.BlogJsonLd(blog.Title, blog.Description, blog.Url, card);
            model["posts"] = posts.Take(PageSize).Select(p => PostModel(blog, p, withBody: false)).ToList();
            model["page"] = new Dictionary<string, object?>
            {
                ["number"] = number,
                ["newer_url"] = number > 1 ? (number == 2 ? "/" : $"/?page={number - 1}") : null,
                ["older_url"] = hasOlder ? $"/?page={number + 1}" : null,
            };
            return await PageAsync(ctx, theme, blog, "home", model, 200);
        });

        app.MapGet("/posts/{slug}", async (HttpContext ctx, string slug, Db db, Theme theme, IConfiguration config) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return NotSetUp(ctx, config);
            var post = await db.ReadAsync(tx => tx.One<PublicPost>($"{SelectLive} AND p.slug = @slug", new { blogId = blog.Id, slug }),
                ctx.RequestAborted);
            if (post is null) return await NotFoundAsync(ctx, theme, blog);
            var description = post.Excerpt.Length > 0 ? post.Excerpt : Markdown.ToPlainText(post.BodyMd, 200);
            var model = Model(blog, Meta(blog, post.Title, description, PostUrl(blog, post.Slug), "article",
                Iso(post.PublishedAt), noindex: false));
            var image = await Social.FirstPictureAsync(db, blog.Id, post.BodyMd, blog.Url, ctx.RequestAborted)
                ?? Social.PostCard(blog.Url, CardTheme(theme, blog), post.Slug, PostCardText(blog, post));
            Image(model, image);
            var terms = Posts.Taxonomy.Parse(post.TermsJson);
            var meta = (Dictionary<string, object?>)model["meta"]!;
            meta["modified_at"] = Iso(post.UpdatedAt);
            meta["tags"] = terms.Where(t => t.Kind == "tag").Select(t => t.Name).ToList();
            meta["json_ld"] = Social.PostJsonLd(blog.Title, blog.Url, post.Title, description, PostUrl(blog, post.Slug), Iso(post.PublishedAt),
                Iso(post.UpdatedAt), image, [.. terms.Where(t => t.Kind == "tag").Select(t => t.Name)], terms.FirstOrDefault(t => t.Kind == "category")?.Name);
            model["post"] = PostModel(blog, post, withBody: true);
            return await PageAsync(ctx, theme, blog, "post", model, 200);
        });

        app.MapGet("/{kind:regex(^(category|tag)$)}/{slug}", async (HttpContext ctx, string kind, string slug, int? page, Db db, Theme theme,
            IConfiguration config) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return NotSetUp(ctx, config);
            var term = (await LiveTermsAsync(db, blog.Id, ctx.RequestAborted)).FirstOrDefault(t => t.Kind == kind && t.Slug == slug);
            if (term is null) return await NotFoundAsync(ctx, theme, blog);
            var number = Math.Max(page ?? 1, 1);
            var posts = await LiveAsync(db, blog.Id, PageSize + 1, (number - 1) * PageSize, ctx.RequestAborted, term.Id);
            var url = TermUrl(blog, kind, slug);
            var label = kind == "category" ? "Category" : "Tag";
            var model = Model(blog, Meta(blog, number == 1 ? $"{term.Name} · {blog.Title}" : $"{term.Name} · page {number} · {blog.Title}",
                term.Description.Length > 0 ? term.Description : $"{label}: {term.Name}", number == 1 ? url : $"{url}?page={number}", "website", null, noindex: false));
            Image(model, Social.BlogCard(blog.Url, CardTheme(theme, blog), BlogCardText(blog)));
            model["term"] = new Dictionary<string, object?> { ["kind"] = kind, ["label"] = label, ["name"] = term.Name, ["description"] = term.Description, ["url"] = url };
            model["posts"] = posts.Take(PageSize).Select(p => PostModel(blog, p, withBody: false)).ToList();
            model["page"] = new Dictionary<string, object?>
            {
                ["number"] = number,
                ["newer_url"] = number > 1 ? (number == 2 ? url : $"{url}?page={number - 1}") : null,
                ["older_url"] = posts.Count > PageSize ? $"{url}?page={number + 1}" : null,
            };
            return await PageAsync(ctx, theme, blog, "term", model, 200);
        });

        // Link cards (docs/specs/social.md): drawn in the blog's own theme, only for what readers can already see.
        app.MapGet("/cards/blog.png", async (HttpContext ctx, Db db, Theme theme, Cards.LinkCards cards, IConfiguration config) =>
            await ResolveAsync(ctx, db, config) is { } blog
                ? Card(ctx, cards, CardTheme(theme, blog), BlogCardText(blog))
                : Results.NotFound());

        app.MapGet("/cards/posts/{slug}.png", async (HttpContext ctx, string slug, Db db, Theme theme, Cards.LinkCards cards, IConfiguration config) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return Results.NotFound();
            var post = await db.ReadAsync(tx => tx.One<PublicPost>($"{SelectLive} AND p.slug = @slug", new { blogId = blog.Id, slug }), ctx.RequestAborted);
            return post is null ? Results.NotFound() : Card(ctx, cards, CardTheme(theme, blog), PostCardText(blog, post));
        });

        app.MapGet("/feed.xml", async (HttpContext ctx, Db db, IConfiguration config, SearchPing ping) =>
            await ResolveAsync(ctx, db, config) is { } blog
                ? Xml(Rss(blog, await LiveAsync(db, blog.Id, FeedSize, 0, ctx.RequestAborted), ping.Hub), "application/rss+xml")
                : Results.NotFound());

        app.MapGet("/atom.xml", async (HttpContext ctx, Db db, IConfiguration config, SearchPing ping) =>
            await ResolveAsync(ctx, db, config) is { } blog
                ? Xml(Atom(blog, await LiveAsync(db, blog.Id, FeedSize, 0, ctx.RequestAborted), ping.Hub), "application/atom+xml")
                : Results.NotFound());

        // The blog's IndexNow key, published where the engines look for it: how they know a ping came from this site.
        app.MapGet("/{key}.txt", async (HttpContext ctx, string key, Db db, IConfiguration config) =>
        {
            if (key.Length != 32 || !key.All(char.IsAsciiHexDigitLower)) return Results.NotFound();
            if (await ResolveAsync(ctx, db, config) is not { } blog) return Results.NotFound();
            var mine = await db.ReadAsync(tx => tx.One<string?>("SELECT settings->>'indexnow_key' FROM blog WHERE id = @Id", new { blog.Id }), ctx.RequestAborted);
            return mine == key ? Results.Text(key, "text/plain; charset=utf-8") : Results.NotFound();
        });

        app.MapGet("/sitemap.xml", async (HttpContext ctx, Db db, IConfiguration config) =>
            await ResolveAsync(ctx, db, config) is { } blog
                ? Xml(Sitemap(blog, await LiveAsync(db, blog.Id, 50_000, 0, ctx.RequestAborted), await LiveTermsAsync(db, blog.Id, ctx.RequestAborted)), "application/xml")
                : Results.NotFound());

        app.MapGet("/robots.txt", async (HttpContext ctx, Db db, IConfiguration config) =>
        {
            var blog = await ResolveAsync(ctx, db, config);
            var sitemap = blog is null ? "" : $"Sitemap: {blog.Url.TrimEnd('/')}/sitemap.xml\n";
            return Results.Text($"User-agent: *\nDisallow: /api/\nDisallow: /admin\nDisallow: /oauth/\nDisallow: /mcp\n{sitemap}", "text/plain; charset=utf-8");
        });

        // Anything else a reader GETs: the theme's not-found page, outside the API and OAuth surfaces. GET only, as a
        // lowest-precedence catch-all: a fallback for every method would outrank routes that some requests don't match
        // (an endpoint taking a JSON body doesn't match a bodiless POST), turning their real answer into this 404.
        app.MapGet("/{**path:nonfile}", async (HttpContext ctx, Db db, Theme theme, IConfiguration config) =>
        {
            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/oauth")
                || path.StartsWithSegments("/mcp") || path.StartsWithSegments("/.well-known"))
                return Results.NotFound();
            return await ResolveAsync(ctx, db, config) is { } blog ? await NotFoundAsync(ctx, theme, blog) : Results.NotFound();
        }).WithOrder(int.MaxValue);
    }

    /// <summary>
    /// Which blog this request is for. Hosted (<c>Blog__Hosting__Domain</c> set): the subdomain names it, and a host
    /// outside the domain or an unknown slug has none. Self-hosted: the instance's blog. A blog hidden from the public
    /// (decision 0005) has none either.
    /// </summary>
    public static async Task<SiteBlog?> ResolveAsync(HttpContext ctx, Db db, IConfiguration config)
    {
        var domain = config["Blog:Hosting:Domain"]?.Trim('.').ToLowerInvariant();
        var host = ctx.Request.Host.Host.ToLowerInvariant();
        string? slug = null;
        if (domain is { Length: > 0 })
        {
            if (!host.EndsWith("." + domain, StringComparison.Ordinal)) return null;
            slug = host[..^(domain.Length + 1)];
            if (slug.Contains('.')) return null;
        }
        const string columns = "id, slug, title, description, coalesce(settings->>'theme', '') AS theme, coalesce((settings->>'theme_picker')::boolean, false) AS theme_picker";
        var row = await db.ReadAsync(tx => tx.One<(Guid Id, string Slug, string Title, string Description, string Theme, bool ThemePicker)?>(
            slug is null
                ? $"SELECT {columns} FROM blog WHERE hidden_at IS NULL ORDER BY created_at LIMIT 1"
                : $"SELECT {columns} FROM blog WHERE hidden_at IS NULL AND slug = @slug",
            new { slug }), ctx.RequestAborted);
        if (row is not { } b) return null;
        var url = domain is { Length: > 0 }
            ? $"https://{b.Slug}.{domain}/"
            : (config["Blog:PublicUrl"] is { Length: > 0 } p ? p.TrimEnd('/') + "/" : $"{ctx.Request.Scheme}://{ctx.Request.Host}/");
        return new SiteBlog(b.Id, b.Slug, b.Title, b.Description, url, b.Theme, b.ThemePicker);
    }

    private static readonly string SelectLive = $"""
        SELECT p.slug, r.title, r.excerpt, r.body_md, p.published_at, p.updated_at, {Posts.PostService.TermsOf("r.term_ids")} AS terms_json
        FROM post p JOIN post_revision r ON r.id = p.current_revision_id
        WHERE p.blog_id = @blogId AND p.status = 'live'
        """;

    private static Task<IReadOnlyList<PublicPost>> LiveAsync(Db db, Guid blogId, int limit, int offset, CancellationToken ct, Guid? termId = null) =>
        db.ReadAsync<IReadOnlyList<PublicPost>>(async tx => [.. await tx.Query<PublicPost>($"""
            {SelectLive}
            {(termId is null ? "" : "AND r.term_ids @> ARRAY[@termId]")}
            ORDER BY p.published_at DESC, p.id
            LIMIT @limit OFFSET @offset
            """, new { blogId, limit, offset, termId })], ct);

    /// <summary>The blog's terms that at least one live post carries: a term with nothing live has no page.</summary>
    private static Task<IReadOnlyList<PublicTerm>> LiveTermsAsync(Db db, Guid blogId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<PublicTerm>>(async tx => [.. await tx.Query<PublicTerm>("""
            SELECT t.id, t.kind, t.slug, t.name, t.description FROM term t
            WHERE t.blog_id = @blogId AND EXISTS (
                SELECT 1 FROM post p JOIN post_revision r ON r.id = p.current_revision_id
                WHERE p.blog_id = t.blog_id AND p.status = 'live' AND r.term_ids @> ARRAY[t.id])
            ORDER BY t.kind, lower(t.name)
            """, new { blogId })], ct);

    private static string TermUrl(SiteBlog blog, string kind, string slug) => $"{blog.Url}{kind}/{slug}";

    private static Dictionary<string, object?> Model(SiteBlog blog, Dictionary<string, object?> meta) => new()
    {
        ["blog"] = new Dictionary<string, object?>
        {
            ["title"] = blog.Title,
            ["description"] = blog.Description,
            ["url"] = blog.Url,
            ["rss_url"] = blog.Url + "feed.xml",
            ["atom_url"] = blog.Url + "atom.xml",
            ["language"] = "en",
        },
        ["meta"] = meta,
    };

    private static Dictionary<string, object?> Meta(SiteBlog blog, string title, string description, string canonical, string ogType,
        string? publishedAt, bool noindex) => new()
    {
        ["title"] = title,
        ["description"] = description.Length > 0 ? description : blog.Title,
        ["canonical"] = canonical,
        ["og_type"] = ogType,
        ["published_at"] = publishedAt,
        ["noindex"] = noindex,
    };

    private static Dictionary<string, object?> PostModel(SiteBlog blog, PublicPost p, bool withBody) => new()
    {
        ["title"] = p.Title,
        ["url"] = PostUrl(blog, p.Slug),
        ["excerpt"] = p.Excerpt,
        ["published_at"] = Iso(p.PublishedAt),
        ["published_display"] = p.PublishedAt.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
        ["body_html"] = withBody ? Markdown.ToHtml(p.BodyMd) : null,
        ["categories"] = TermModels(blog, p, "category"),
        ["tags"] = TermModels(blog, p, "tag"),
    };

    private static List<Dictionary<string, object?>> TermModels(SiteBlog blog, PublicPost p, string kind) =>
        [.. Posts.Taxonomy.Parse(p.TermsJson).Where(t => t.Kind == kind).Select(t => new Dictionary<string, object?>
        {
            ["name"] = t.Name,
            ["url"] = TermUrl(blog, t.Kind, t.Slug),
        })];

    private static string PostUrl(SiteBlog blog, string slug) => $"{blog.Url}posts/{slug}";

    // ------------------------------------------------------------------------------------------- link cards

    /// <summary>Cards are drawn in the blog's own theme, never a reader's preview of another.</summary>
    private static string CardTheme(Theme theme, SiteBlog blog) => theme.Exists(blog.Theme) ? blog.Theme : Theme.Default;

    private static Cards.CardText PostCardText(SiteBlog blog, PublicPost p) =>
        new(blog.Title, p.Title, Social.Day(p.PublishedAt), Social.Host(blog.Url));

    private static Cards.CardText BlogCardText(SiteBlog blog) =>
        new(Social.Host(blog.Url), blog.Title, blog.Description, "");

    private static void Image(Dictionary<string, object?> model, ShareImage image) =>
        ((Dictionary<string, object?>)model["meta"]!)["image"] = new Dictionary<string, object?>
        {
            ["url"] = image.Url, ["alt"] = image.Alt, ["width"] = image.Width, ["height"] = image.Height,
        };

    private static IResult Card(HttpContext ctx, Cards.LinkCards cards, string theme, Cards.CardText text)
    {
        var etag = $"\"{text.Version(theme)}\"";
        var h = ctx.Response.Headers;
        h.ETag = etag;
        // The address carries the version, so a changed title is a new address: a day is safe to keep.
        h.CacheControl = "public, max-age=86400";
        h["X-Content-Type-Options"] = "nosniff";
        if (ctx.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(304);
        return Results.Bytes(cards.Render(theme, text), "image/png");
    }

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static async Task<IResult> PageAsync(HttpContext ctx, Theme theme, SiteBlog blog, string page, Dictionary<string, object?> model, int status)
    {
        var h = ctx.Response.Headers;
        var choice = await ChooseThemeAsync(ctx, theme, blog);
        model["theme"] = new Dictionary<string, object?>
        {
            ["name"] = choice.Shown,
            ["label"] = theme.Info(choice.Shown).Label,
            ["css_url"] = $"/assets/themes/{choice.Shown}.css",
        };
        if (choice.Bar)
        {
            model["picker"] = PickerModel(ctx, theme, blog, choice, model.GetValueOrDefault("latest_post_url") as string);
            model["picker_html"] = await theme.RenderSharedAsync("picker", model);
            // The same address shows another theme to a browser that picked one: no shared cache may mix them up.
            h.Vary = "Cookie";
        }
        // A preview is this reader's own view: never indexed, never cached for anyone else.
        if (choice.Previewing && model["meta"] is Dictionary<string, object?> meta) meta["noindex"] = true;

        model["head_html"] = await theme.RenderSharedAsync("head", model);
        h.ContentSecurityPolicy = "default-src 'none'; img-src https: 'self'; style-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "strict-origin-when-cross-origin";
        h.CacheControl = status != 200 ? "no-store" : choice.Previewing || choice.Owner ? "private, no-store" : "public, max-age=60";
        return Results.Content(await theme.RenderAsync(choice.Shown, page, model), "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    /// <param name="Shown">The theme this response renders in.</param>
    /// <param name="Previewing">Not the blog's own theme: a reader's (or the owner's) preview.</param>
    /// <param name="Bar">Whether the theme bar shows: readers may try themes, or the owner is looking.</param>
    /// <param name="Owner">The blog's owner, signed in, is the one looking.</param>
    private sealed record ThemeChoice(string Shown, string Own, bool Previewing, bool Bar, bool Owner);

    /// <summary>
    /// Which theme a page renders in. The blog's own, unless trying themes is allowed here (the owner's switch, or the
    /// owner signed in) and this browser asked for another: <c>?theme=name</c> picks one and remembers it in a session
    /// cookie, so it follows the reader from page to page; <c>?theme=own</c> (or the blog's own name) goes back.
    /// Without the switch, the parameter and the cookie are ignored and nothing is set.
    /// </summary>
    private static async Task<ThemeChoice> ChooseThemeAsync(HttpContext ctx, Theme theme, SiteBlog blog)
    {
        var own = theme.Exists(blog.Theme) ? blog.Theme : Theme.Default;
        var owner = ctx.Request.Cookies.ContainsKey(AgentBlog.Auth.Sessions.Cookie) && await IsOwnerAsync(ctx, blog);
        if (!blog.ThemePicker && !owner) return new(own, own, false, false, false);

        string? want = null;
        var asked = ctx.Request.Query["theme"].ToString();
        if (asked.Length > 0)
        {
            if (theme.Exists(asked) && asked != own)
            {
                want = asked;
                ctx.Response.Cookies.Append(PreviewCookie, asked, new CookieOptions
                {
                    Path = "/", HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps, IsEssential = true,
                });
            }
            else if (ctx.Request.Cookies.ContainsKey(PreviewCookie))
            {
                ctx.Response.Cookies.Delete(PreviewCookie, new CookieOptions { Path = "/" });
            }
        }
        else if (ctx.Request.Cookies[PreviewCookie] is { } remembered && theme.Exists(remembered) && remembered != own)
        {
            want = remembered;
        }
        return new(want ?? own, own, want is not null, true, owner);
    }

    private static Dictionary<string, object?> PickerModel(HttpContext ctx, Theme theme, SiteBlog blog, ThemeChoice choice, string? latestPostUrl)
    {
        // Links keep the reader on the page they're on (and its page number).
        var path = ctx.Request.Path.Value is { Length: > 0 } p ? p : "/";
        var pageNumber = ctx.Request.Query["page"].ToString();
        string Here(string name) => $"{path}?theme={name}{(int.TryParse(pageNumber, out var n) && n > 1 ? $"&page={n}" : "")}";
        return new Dictionary<string, object?>
        {
            ["previewing"] = choice.Previewing,
            ["owner_only"] = choice.Owner && !blog.ThemePicker,
            ["previewing_label"] = theme.Info(choice.Shown).Label,
            ["own_url"] = Here("own"),
            ["gallery_url"] = blog.Url + "themes",
            ["themes"] = theme.All.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["label"] = t.Label,
                ["description"] = t.Description,
                ["url"] = Here(t.Name == choice.Own ? "own" : t.Name),
                ["home_url"] = $"{blog.Url}?theme={(t.Name == choice.Own ? "own" : t.Name)}",
                ["post_url"] = latestPostUrl is null ? null : $"{latestPostUrl}?theme={(t.Name == choice.Own ? "own" : t.Name)}",
                ["active"] = t.Name == choice.Shown,
                ["is_blog_theme"] = t.Name == choice.Own,
            }).ToList(),
        };
    }

    /// <summary>Whether the signed-in browser is this blog's owner (for previews while readers can't try themes).</summary>
    private static async Task<bool> IsOwnerAsync(HttpContext ctx, SiteBlog blog)
    {
        var session = await ctx.RequestServices.GetRequiredService<AgentBlog.Auth.Sessions>().AuthenticateAsync(ctx, ctx.RequestAborted);
        if (session is null) return false;
        return await ctx.RequestServices.GetRequiredService<Db>().ReadAsync(tx => tx.One<bool>("""
            SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @blogId AND account_id = @accountId AND role = 'owner')
            """, new { blogId = blog.Id, accountId = session.AccountId }), ctx.RequestAborted);
    }

    private static IResult Css(HttpContext ctx, string css)
    {
        ctx.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.Text(css, "text/css; charset=utf-8");
    }

    private static Task<IResult> NotFoundAsync(HttpContext ctx, Theme theme, SiteBlog blog) =>
        PageAsync(ctx, theme, blog, "not-found", Model(blog, Meta(blog, "Not found", "", blog.Url, "website", null, noindex: true)), 404);

    /// <summary>An instance with no blog yet (self-hosted, before setup): say so, and where setup is.</summary>
    private static IResult NotSetUp(HttpContext ctx, IConfiguration config) =>
        config["Blog:Hosting:Domain"] is { Length: > 0 }
            ? Results.NotFound()
            : Results.Content("""<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Not set up yet</title></head><body><p>This blog isn't set up yet. Its owner can <a href="/setup">set it up</a>.</p></body></html>""",
                "text/html; charset=utf-8", Encoding.UTF8, 404);

    // ---------------------------------------------------------------------------------------------------- feeds

    private static IResult Xml(string xml, string type)
    {
        return Results.Text(xml, type + "; charset=utf-8", Encoding.UTF8);
    }

    private static XmlWriterSettings Settings => new() { Encoding = new UTF8Encoding(false), Indent = true, OmitXmlDeclaration = false };

    private static string Write(Action<XmlWriter> body)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(new StringWriterUtf8(sb), Settings)) body(w);
        return sb.ToString();
    }

    private sealed class StringWriterUtf8(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }

    private static string Rss(SiteBlog blog, IReadOnlyList<PublicPost> posts, string? hub) => Write(w =>
    {
        w.WriteStartElement("rss");
        w.WriteAttributeString("version", "2.0");
        w.WriteAttributeString("xmlns", "atom", null, "http://www.w3.org/2005/Atom");
        w.WriteAttributeString("xmlns", "content", null, "http://purl.org/rss/1.0/modules/content/");
        w.WriteStartElement("channel");
        w.WriteElementString("title", blog.Title);
        w.WriteElementString("link", blog.Url);
        w.WriteElementString("description", blog.Description.Length > 0 ? blog.Description : blog.Title);
        w.WriteStartElement("atom", "link", "http://www.w3.org/2005/Atom");
        w.WriteAttributeString("href", blog.Url + "feed.xml");
        w.WriteAttributeString("rel", "self");
        w.WriteAttributeString("type", "application/rss+xml");
        w.WriteEndElement();
        // WebSub: readers that subscribe through the hub hear about a new post at once (docs/specs/social.md § Found).
        if (hub is not null)
        {
            w.WriteStartElement("atom", "link", "http://www.w3.org/2005/Atom");
            w.WriteAttributeString("href", hub);
            w.WriteAttributeString("rel", "hub");
            w.WriteEndElement();
        }
        if (posts.Count > 0) w.WriteElementString("lastBuildDate", posts.Max(p => p.UpdatedAt).ToUniversalTime().ToString("R", CultureInfo.InvariantCulture));
        foreach (var p in posts)
        {
            w.WriteStartElement("item");
            w.WriteElementString("title", p.Title);
            w.WriteElementString("link", PostUrl(blog, p.Slug));
            w.WriteStartElement("guid");
            w.WriteAttributeString("isPermaLink", "true");
            w.WriteString(PostUrl(blog, p.Slug));
            w.WriteEndElement();
            w.WriteElementString("pubDate", DateTime.SpecifyKind(p.PublishedAt, DateTimeKind.Utc).ToString("R", CultureInfo.InvariantCulture));
            w.WriteElementString("description", p.Excerpt.Length > 0 ? p.Excerpt : Markdown.ToPlainText(p.BodyMd, 300));
            w.WriteStartElement("content", "encoded", "http://purl.org/rss/1.0/modules/content/");
            w.WriteCData(Markdown.ToHtml(p.BodyMd, new Uri(blog.Url)).Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal));
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();
    });

    private static string Atom(SiteBlog blog, IReadOnlyList<PublicPost> posts, string? hub) => Write(w =>
    {
        const string ns = "http://www.w3.org/2005/Atom";
        w.WriteStartElement("feed", ns);
        w.WriteElementString("title", ns, blog.Title);
        w.WriteElementString("id", ns, blog.Url);
        w.WriteElementString("updated", ns, Iso(posts.Count > 0 ? posts.Max(p => p.UpdatedAt) : DateTime.UnixEpoch));
        Link(w, ns, blog.Url, "alternate");
        Link(w, ns, blog.Url + "atom.xml", "self");
        if (hub is not null) Link(w, ns, hub, "hub");
        foreach (var p in posts)
        {
            w.WriteStartElement("entry", ns);
            w.WriteElementString("title", ns, p.Title);
            w.WriteElementString("id", ns, PostUrl(blog, p.Slug));
            Link(w, ns, PostUrl(blog, p.Slug), "alternate");
            w.WriteElementString("published", ns, Iso(p.PublishedAt));
            w.WriteElementString("updated", ns, Iso(p.UpdatedAt));
            w.WriteStartElement("author", ns);
            w.WriteElementString("name", ns, blog.Title);
            w.WriteEndElement();
            if (p.Excerpt.Length > 0) w.WriteElementString("summary", ns, p.Excerpt);
            w.WriteStartElement("content", ns);
            w.WriteAttributeString("type", "html");
            w.WriteString(Markdown.ToHtml(p.BodyMd, new Uri(blog.Url)));
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    private static void Link(XmlWriter w, string ns, string href, string rel)
    {
        w.WriteStartElement("link", ns);
        w.WriteAttributeString("href", href);
        w.WriteAttributeString("rel", rel);
        w.WriteEndElement();
    }

    private static string Sitemap(SiteBlog blog, IReadOnlyList<PublicPost> posts, IReadOnlyList<PublicTerm> terms) => Write(w =>
    {
        const string ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        w.WriteStartElement("urlset", ns);
        w.WriteStartElement("url", ns);
        w.WriteElementString("loc", ns, blog.Url);
        w.WriteEndElement();
        foreach (var p in posts)
        {
            w.WriteStartElement("url", ns);
            w.WriteElementString("loc", ns, PostUrl(blog, p.Slug));
            w.WriteElementString("lastmod", ns, Iso(p.UpdatedAt));
            w.WriteEndElement();
        }
        foreach (var t in terms)
        {
            w.WriteStartElement("url", ns);
            w.WriteElementString("loc", ns, TermUrl(blog, t.Kind, t.Slug));
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });
}
