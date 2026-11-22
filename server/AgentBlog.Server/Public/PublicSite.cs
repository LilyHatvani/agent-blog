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

    public sealed record SiteBlog(Guid Id, string Slug, string Title, string Description, string Url);

    private sealed record PublicPost(string Slug, string Title, string Excerpt, string BodyMd, DateTime PublishedAt, DateTime UpdatedAt);

    public static void MapPublicSite(this WebApplication app)
    {
        app.MapGet("/assets/theme.css", (Theme theme) => Results.Text(theme.Css, "text/css; charset=utf-8"));

        app.MapGet("/", async (HttpContext ctx, Db db, Theme theme, IConfiguration config, int? page) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return NotSetUp(ctx, config);
            var number = Math.Max(page ?? 1, 1);
            var posts = await LiveAsync(db, blog.Id, PageSize + 1, (number - 1) * PageSize, ctx.RequestAborted);
            var hasOlder = posts.Count > PageSize;
            var model = Model(blog, Meta(blog, number == 1 ? blog.Title : $"{blog.Title} · page {number}",
                blog.Description, number == 1 ? blog.Url : $"{blog.Url}?page={number}", "website", null, noindex: false));
            model["posts"] = posts.Take(PageSize).Select(p => PostModel(blog, p, withBody: false)).ToList();
            model["page"] = new Dictionary<string, object?>
            {
                ["number"] = number,
                ["newer_url"] = number > 1 ? (number == 2 ? "/" : $"/?page={number - 1}") : null,
                ["older_url"] = hasOlder ? $"/?page={number + 1}" : null,
            };
            return await PageAsync(ctx, theme, "home", model, 200);
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
            model["post"] = PostModel(blog, post, withBody: true);
            return await PageAsync(ctx, theme, "post", model, 200);
        });

        app.MapGet("/feed.xml", async (HttpContext ctx, Db db, IConfiguration config) =>
            await ResolveAsync(ctx, db, config) is { } blog
                ? Xml(Rss(blog, await LiveAsync(db, blog.Id, FeedSize, 0, ctx.RequestAborted)), "application/rss+xml")
                : Results.NotFound());

        app.MapGet("/atom.xml", async (HttpContext ctx, Db db, IConfiguration config) =>
            await ResolveAsync(ctx, db, config) is { } blog
                ? Xml(Atom(blog, await LiveAsync(db, blog.Id, FeedSize, 0, ctx.RequestAborted)), "application/atom+xml")
                : Results.NotFound());

        app.MapGet("/sitemap.xml", async (HttpContext ctx, Db db, IConfiguration config) =>
            await ResolveAsync(ctx, db, config) is { } blog
                ? Xml(Sitemap(blog, await LiveAsync(db, blog.Id, 50_000, 0, ctx.RequestAborted)), "application/xml")
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
        var row = await db.ReadAsync(tx => tx.One<(Guid Id, string Slug, string Title, string Description)?>(
            slug is null
                ? "SELECT id, slug, title, description FROM blog WHERE hidden_at IS NULL ORDER BY created_at LIMIT 1"
                : "SELECT id, slug, title, description FROM blog WHERE hidden_at IS NULL AND slug = @slug",
            new { slug }), ctx.RequestAborted);
        if (row is not { } b) return null;
        var url = domain is { Length: > 0 }
            ? $"https://{b.Slug}.{domain}/"
            : (config["Blog:PublicUrl"] is { Length: > 0 } p ? p.TrimEnd('/') + "/" : $"{ctx.Request.Scheme}://{ctx.Request.Host}/");
        return new SiteBlog(b.Id, b.Slug, b.Title, b.Description, url);
    }

    private const string SelectLive = """
        SELECT p.slug, r.title, r.excerpt, r.body_md, p.published_at, p.updated_at
        FROM post p JOIN post_revision r ON r.id = p.current_revision_id
        WHERE p.blog_id = @blogId AND p.status = 'live'
        """;

    private static Task<IReadOnlyList<PublicPost>> LiveAsync(Db db, Guid blogId, int limit, int offset, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<PublicPost>>(async tx => [.. await tx.Query<PublicPost>($"""
            {SelectLive}
            ORDER BY p.published_at DESC, p.id
            LIMIT @limit OFFSET @offset
            """, new { blogId, limit, offset })], ct);

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
    };

    private static string PostUrl(SiteBlog blog, string slug) => $"{blog.Url}posts/{slug}";

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static async Task<IResult> PageAsync(HttpContext ctx, Theme theme, string page, Dictionary<string, object?> model, int status)
    {
        var h = ctx.Response.Headers;
        h.ContentSecurityPolicy = "default-src 'none'; img-src https: 'self'; style-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "strict-origin-when-cross-origin";
        h.CacheControl = status == 200 ? "public, max-age=60" : "no-store";
        return Results.Content(await theme.RenderAsync(page, model), "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    private static Task<IResult> NotFoundAsync(HttpContext ctx, Theme theme, SiteBlog blog) =>
        PageAsync(ctx, theme, "not-found", Model(blog, Meta(blog, "Not found", "", blog.Url, "website", null, noindex: true)), 404);

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

    private static string Rss(SiteBlog blog, IReadOnlyList<PublicPost> posts) => Write(w =>
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

    private static string Atom(SiteBlog blog, IReadOnlyList<PublicPost> posts) => Write(w =>
    {
        const string ns = "http://www.w3.org/2005/Atom";
        w.WriteStartElement("feed", ns);
        w.WriteElementString("title", ns, blog.Title);
        w.WriteElementString("id", ns, blog.Url);
        w.WriteElementString("updated", ns, Iso(posts.Count > 0 ? posts.Max(p => p.UpdatedAt) : DateTime.UnixEpoch));
        Link(w, ns, blog.Url, "alternate");
        Link(w, ns, blog.Url + "atom.xml", "self");
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

    private static string Sitemap(SiteBlog blog, IReadOnlyList<PublicPost> posts) => Write(w =>
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
        w.WriteEndElement();
    });
}
