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

    private sealed record PublicPost(Guid Id, string Slug, string Title, string Excerpt, string BodyMd, DateTime PublishedAt, DateTime UpdatedAt, string TermsJson);

    private sealed record PublicTerm(Guid Id, string Kind, string Slug, string Name, string Description);

    public static void MapPublicSite(this WebApplication app)
    {
        // Theme styles. /assets/theme.css is Paper's, for pages cached from before themes.
        app.MapGet("/assets/theme.css", (HttpContext ctx, Theme theme) => Css(ctx, theme.Css(Theme.Default)));
        app.MapGet("/assets/themes/_picker.css", (HttpContext ctx, Theme theme) => Css(ctx, theme.PickerCss));
        app.MapGet("/assets/themes/_subscribe.css", (HttpContext ctx, Theme theme) => Css(ctx, theme.SubscribeCss));
        MapSubscribe(app);
        // A theme's own fonts (Midnight's), served from this site, so no reader's address goes to a font host.
        app.MapGet("/assets/themes/{name}/fonts/{file:regex(^[a-z0-9-]+\\.woff2$)}", (HttpContext ctx, string name, string file, Theme theme) =>
        {
            if (theme.Font(name, file) is not { } bytes) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=2592000";
            return Results.Bytes(bytes, "font/woff2");
        });
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

        app.MapGet("/posts/{slug}", async (HttpContext ctx, string slug, Db db, Theme theme, IConfiguration config, Posts.Bylines bylines,
            Mail.Subscribers subscribers) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return NotSetUp(ctx, config);
            var post = await db.ReadAsync(tx => tx.One<PublicPost>($"{SelectLive} AND p.slug = @slug", new { blogId = blog.Id, slug }),
                ctx.RequestAborted);
            if (post is null) return await NotFoundAsync(ctx, theme, blog);
            var description = post.Excerpt.Length > 0 ? post.Excerpt : Markdown.ToPlainText(post.BodyMd, 200);
            var model = Model(blog, Meta(blog, post.Title, description, PostUrl(blog, post.Slug), "article",
                Iso(post.PublishedAt), noindex: false));
            var image = await Social.PostImageAsync(db, blog.Id, blog.Title, CardTheme(theme, blog), blog.Url, post.Slug, post.Title,
                post.BodyMd, post.PublishedAt, ctx.RequestAborted);
            Image(model, image);
            var terms = Posts.Taxonomy.Parse(post.TermsJson);
            var meta = (Dictionary<string, object?>)model["meta"]!;
            meta["modified_at"] = Iso(post.UpdatedAt);
            meta["tags"] = terms.Where(t => t.Kind == "tag").Select(t => t.Name).ToList();
            // Who wrote it and who let it out (docs/specs/bylines.md), unless the owner turned bylines off.
            var byline = await bylines.OnAsync(blog.Id, ctx.RequestAborted) ? await bylines.ForPostAsync(blog.Id, post.Id, ctx.RequestAborted) : null;
            meta["json_ld"] = Social.PostJsonLd(blog.Title, blog.Url, post.Title, description, PostUrl(blog, post.Slug), Iso(post.PublishedAt),
                Iso(post.UpdatedAt), image, [.. terms.Where(t => t.Kind == "tag").Select(t => t.Name)], terms.FirstOrDefault(t => t.Kind == "category")?.Name,
                byline);
            var postModel = PostModel(blog, post, withBody: true);
            if (byline is not null)
            {
                // The sentence, and the same sentence in parts: each name links to its person's author page.
                var model2 = Posts.Bylines.Model(byline, blog.Url);
                postModel["byline"] = byline.Text;
                postModel["byline_parts"] = model2["parts"];
                postModel["credits"] = model2["credits"];
                postModel["authors"] = model2["authors"];
            }
            model["post"] = postModel;
            // The subscribe form under the post, only while the blog sends email (docs/specs/subscribe.md).
            if (await subscribers.OpenAsync(blog.Id, ctx.RequestAborted)) model["subscribe_form_html"] = await theme.RenderSharedAsync("subscribe-form", model);
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

        // An author's page (docs/specs/authors.md): who they are and the live posts they or their agents wrote, newest first.
        // Only while the blog shows bylines (readers see no names otherwise), and only once something of theirs is live.
        app.MapGet("/author/{slug}", async (HttpContext ctx, string slug, int? page, Db db, Theme theme, IConfiguration config,
            Posts.Bylines bylines) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return NotSetUp(ctx, config);
            if (!await bylines.OnAsync(blog.Id, ctx.RequestAborted)) return await NotFoundAsync(ctx, theme, blog);
            var author = await db.ReadAsync(tx => tx.One<AuthorRow>("""
                SELECT au.account_id, au.slug, au.profile_url,
                       CASE WHEN m.role = 'owner' THEN coalesce(nullif(b.settings->>'author_name', ''), a.display_name) ELSE a.display_name END AS name
                FROM author au JOIN account a ON a.id = au.account_id JOIN blog b ON b.id = au.blog_id
                LEFT JOIN blog_member m ON m.blog_id = au.blog_id AND m.account_id = au.account_id
                WHERE au.blog_id = @blogId AND au.slug = @slug
                """, new { blogId = blog.Id, slug }), ctx.RequestAborted);
            if (author is null) return await NotFoundAsync(ctx, theme, blog);
            var theirs = await AuthorPostsAsync(db, bylines, blog.Id, author.AccountId, ctx.RequestAborted);
            if (theirs.Count == 0) return await NotFoundAsync(ctx, theme, blog);

            var number = Math.Max(page ?? 1, 1);
            var url = Posts.Bylines.AuthorUrl(blog.Url, author.Slug)!;
            var agentNames = theirs.SelectMany(t => t.Byline.Writers).Where(c => c.Kind == "agent" && c.Person == author.AccountId)
                .Select(c => c.Short).Distinct().ToList();
            var with = agentNames.Count == 0 ? "" : $", with {Posts.Bylines.List(agentNames)}";
            var description = $"{author.Name} writes for {blog.Title}{with}: {theirs.Count} post{(theirs.Count == 1 ? "" : "s")}.";
            var model = Model(blog, Meta(blog, number == 1 ? $"{author.Name} · {blog.Title}" : $"{author.Name} · page {number} · {blog.Title}",
                description, number == 1 ? url : $"{url}?page={number}", "profile", null, noindex: false));
            Image(model, Social.BlogCard(blog.Url, CardTheme(theme, blog), BlogCardText(blog)));
            model["author"] = new Dictionary<string, object?>
            {
                ["name"] = author.Name, ["url"] = url, ["agents"] = agentNames, ["post_count"] = theirs.Count, ["description"] = description,
                ["profile_url"] = author.ProfileUrl,
                ["initial"] = author.Name.Trim() is { Length: > 0 } n ? char.ToUpperInvariant(n[0]).ToString() : "?",
            };
            var shown = theirs.Skip((number - 1) * PageSize).Take(PageSize).ToList();
            if (shown.Count == 0 && number > 1) return await NotFoundAsync(ctx, theme, blog);
            model["posts"] = shown.Select(t =>
            {
                var m = PostModel(blog, t.Post, withBody: false);
                m["byline"] = t.Byline.Text;
                return m;
            }).ToList();
            model["page"] = new Dictionary<string, object?>
            {
                ["number"] = number,
                ["newer_url"] = number > 1 ? (number == 2 ? url : $"{url}?page={number - 1}") : null,
                ["older_url"] = theirs.Count > number * PageSize ? $"{url}?page={number + 1}" : null,
            };
            return await PageAsync(ctx, theme, blog, "author", model, 200);
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

        app.MapGet("/feed.xml", async (HttpContext ctx, Db db, IConfiguration config, SearchPing ping, Posts.Bylines bylines) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return Results.NotFound();
            var posts = await LiveAsync(db, blog.Id, FeedSize, 0, ctx.RequestAborted);
            return Xml(Rss(blog, posts, ping.Hub, await FeedBylinesAsync(bylines, blog, posts, ctx.RequestAborted)), "application/rss+xml");
        });

        app.MapGet("/atom.xml", async (HttpContext ctx, Db db, IConfiguration config, SearchPing ping, Posts.Bylines bylines) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return Results.NotFound();
            var posts = await LiveAsync(db, blog.Id, FeedSize, 0, ctx.RequestAborted);
            return Xml(Atom(blog, posts, ping.Hub, await FeedBylinesAsync(bylines, blog, posts, ctx.RequestAborted)), "application/atom+xml");
        });

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

        // Echo of Dreams' blog served its feed and sitemap under /api/blog/ (docs/specs/old-addresses.md): readers' feed
        // apps and search engines keep finding them.
        app.MapGet("/api/blog/{name:regex(^(feed|sitemap)\\.xml$)}", (string name) => Results.Redirect("/" + name, permanent: true));

        // Anything else a reader GETs: the theme's not-found page, outside the API and OAuth surfaces. GET only, as a
        // lowest-precedence catch-all: a fallback for every method would outrank routes that some requests don't match
        // (an endpoint taking a JSON body doesn't match a bodiless POST), turning their real answer into this 404.
        app.MapGet("/{**path:nonfile}", async (HttpContext ctx, Db db, Theme theme, IConfiguration config) =>
        {
            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/oauth")
                || path.StartsWithSegments("/mcp") || path.StartsWithSegments("/.well-known"))
                return Results.NotFound();
            // An address the blog had before (docs/specs/old-addresses.md) answers with where that page is now.
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
        SELECT p.id, p.slug, r.title, r.excerpt, r.body_md, p.published_at, p.updated_at, {Posts.PostService.TermsOf("r.term_ids")} AS terms_json
        FROM post p JOIN post_revision r ON r.id = p.current_revision_id
        WHERE p.blog_id = @blogId AND p.status = 'live'
        """;

    private sealed record AuthorRow(Guid AccountId, string Slug, string? ProfileUrl, string Name);

    /// <summary>
    /// Where an old address's page lives now, or null: a recorded old path (exact, or without its trailing slash), then
    /// a live post's old <c>/{slug}</c>, then <c>/login</c>. Only ever a path on this blog.
    /// </summary>
    private static Task<string?> OldAddressAsync(Db db, SiteBlog blog, string path, CancellationToken ct) => db.ReadAsync(async tx =>
    {
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        if (await tx.One<string?>("SELECT target FROM old_address WHERE blog_id = @Id AND path IN (@path, @trimmed) ORDER BY path = @path DESC LIMIT 1",
                new { blog.Id, path, trimmed }) is { } target)
            return target;
        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is [var slug] && await tx.One<bool>("SELECT EXISTS (SELECT 1 FROM post WHERE blog_id = @Id AND slug = @slug AND status = 'live')",
                new { blog.Id, slug }))
            return "/posts/" + slug;
        return trimmed == "/login" ? "/sign-in" : null;
    }, ct);

    /// <summary>
    /// The live posts a person wrote, themselves or through their agents, newest first: the same "wrote" their posts'
    /// bylines say (a restore or a decided tag suggestion writes nothing), so the page and the bylines agree.
    /// </summary>
    private static async Task<List<(PublicPost Post, Posts.Byline Byline)>> AuthorPostsAsync(Db db, Posts.Bylines bylines, Guid blogId,
        Guid personId, CancellationToken ct)
    {
        var candidates = await db.ReadAsync(tx => tx.Query<PublicPost>($"""
            {SelectLive}
            AND EXISTS (SELECT 1 FROM post_revision rv WHERE rv.post_id = p.id
                        AND (rv.actor = 'account:' || @personId::text
                             OR rv.actor IN (SELECT 'connection:' || c.id::text FROM connection c WHERE c.blog_id = p.blog_id AND c.created_by = @personId)))
            ORDER BY p.published_at DESC, p.id
            LIMIT 2000
            """, new { blogId, personId }), ct);
        var list = candidates.ToList();
        var lines = await bylines.ForPostsAsync(blogId, [.. list.Select(p => p.Id)], ct);
        return [.. list.Where(p => lines.TryGetValue(p.Id, out var b) && b.Writers.Any(c => c.Person == personId)).Select(p => (p, lines[p.Id]))];
    }

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

    // ------------------------------------------------------------------------------------------------ subscribe

    /// <summary>
    /// Subscribing by email (docs/specs/subscribe.md): the form and its answer, the confirmation page (a button, so a mail
    /// scanner that opens links confirms nobody), and unsubscribing, by page or by a mail client's one-click POST
    /// (RFC 8058). The pages never say whether an address is on the list.
    /// </summary>
    private static void MapSubscribe(WebApplication app)
    {
        app.MapGet("/subscribe", async (HttpContext ctx, Db db, Theme theme, IConfiguration config, Mail.Subscribers subscribers) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return NotSetUp(ctx, config);
            var open = await subscribers.OpenAsync(blog.Id, ctx.RequestAborted);
            return await SubscribePageAsync(ctx, theme, blog, open ? "form" : "off", open ? 200 : 404);
        });

        app.MapPost("/subscribe", async (HttpContext ctx, Db db, Theme theme, IConfiguration config, Mail.Subscribers subscribers,
            Mail.SubscribeLimit limit, TimeProvider clock) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return NotSetUp(ctx, config);
            if (!await subscribers.OpenAsync(blog.Id, ctx.RequestAborted)) return await SubscribePageAsync(ctx, theme, blog, "off", 404);
            if (!limit.Allow(AgentBlog.Auth.SignIn.ClientAddress(ctx, config.GetValue("Blog:Proxy:ForwardedForHops", 0)), clock.GetUtcNow()))
            {
                ctx.Response.Headers.RetryAfter = "60";
                return await SubscribePageAsync(ctx, theme, blog, "limited", 429);
            }
            var form = ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync(ctx.RequestAborted) : null;
            var email = form?["email"].ToString() ?? "";
            // A field people don't see: a bot that fills it is told it worked, and nothing happens.
            if (!string.IsNullOrEmpty(form?["website"].ToString())) return await SubscribePageAsync(ctx, theme, blog, "sent", 200);
            return await subscribers.SubscribeAsync(blog.Id, email, ctx.RequestAborted) switch
            {
                Mail.Subscribers.Outcome.Invalid => await SubscribePageAsync(ctx, theme, blog, "invalid", 400, email: email),
                Mail.Subscribers.Outcome.Off => await SubscribePageAsync(ctx, theme, blog, "off", 404),
                _ => await SubscribePageAsync(ctx, theme, blog, "sent", 200),
            };
        });

        app.MapGet("/subscribe/confirm", async (HttpContext ctx, Guid? s, string? t, Db db, Theme theme, IConfiguration config, Mail.Subscribers subscribers) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return NotSetUp(ctx, config);
            return s is { } id && t is { } token && await subscribers.ConfirmableAsync(blog.Id, id, token, ctx.RequestAborted)
                ? await SubscribePageAsync(ctx, theme, blog, "confirm", 200, action: blog.Url + "subscribe/confirm", s: id.ToString(), t: token)
                : await SubscribePageAsync(ctx, theme, blog, "expired", 404);
        });

        app.MapPost("/subscribe/confirm", async (HttpContext ctx, Db db, Theme theme, IConfiguration config, Mail.Subscribers subscribers) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return NotSetUp(ctx, config);
            var form = ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync(ctx.RequestAborted) : null;
            return Guid.TryParse(form?["s"].ToString(), out var id) && form!["t"].ToString() is { Length: > 0 } token
                   && await subscribers.ConfirmAsync(blog.Id, id, token, ctx.RequestAborted)
                ? await SubscribePageAsync(ctx, theme, blog, "confirmed", 200)
                : await SubscribePageAsync(ctx, theme, blog, "expired", 404);
        });

        app.MapGet("/unsubscribe", async (HttpContext ctx, Guid? s, string? t, Db db, Theme theme, IConfiguration config, Mail.Subscribers subscribers) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return NotSetUp(ctx, config);
            return s is { } id && t is { } token && await subscribers.UnsubscribeTokenValidAsync(blog.Id, id, token, ctx.RequestAborted)
                ? await SubscribePageAsync(ctx, theme, blog, "unsubscribe", 200, action: $"{blog.Url}unsubscribe?s={id:D}&t={Uri.EscapeDataString(token)}")
                : await SubscribePageAsync(ctx, theme, blog, "expired", 404);
        });

        // A form's button, or a mail client's one-click POST (body "List-Unsubscribe=One-Click"): either way, done.
        app.MapPost("/unsubscribe", async (HttpContext ctx, Guid? s, string? t, Db db, Theme theme, IConfiguration config, Mail.Subscribers subscribers) =>
        {
            if (await ResolveAsync(ctx, db, config) is not { } blog) return NotSetUp(ctx, config);
            return s is { } id && t is { } token && await subscribers.UnsubscribeAsync(blog.Id, id, token, ctx.RequestAborted)
                ? await SubscribePageAsync(ctx, theme, blog, "unsubscribed", 200)
                : await SubscribePageAsync(ctx, theme, blog, "expired", 404);
        });
    }

    private static async Task<IResult> SubscribePageAsync(HttpContext ctx, Theme theme, SiteBlog blog, string state, int status,
        string? action = null, string? s = null, string? t = null, string? email = null)
    {
        var model = Model(blog, Meta(blog, $"Subscribe · {blog.Title}", $"Get new posts from {blog.Title} by email.", blog.Url + "subscribe", "website",
            null, noindex: state != "form"));
        model["subscribe"] = new Dictionary<string, object?> { ["state"] = state, ["action"] = action, ["s"] = s, ["t"] = t, ["email"] = email };
        if (state is "form" or "invalid") model["subscribe_form_html"] = await theme.RenderSharedAsync("subscribe-form", model);
        var result = await PageAsync(ctx, theme, blog, "subscribe", model, status);
        // A page with a link's token in it is the reader's own: never cached, never sent on as a referrer.
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        return result;
    }

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

    private static Cards.CardText PostCardText(SiteBlog blog, PublicPost p) => Social.PostCardText(blog.Title, blog.Url, p.Title, p.PublishedAt);

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
        h.ContentSecurityPolicy = "default-src 'none'; img-src https: 'self'; style-src 'self'; font-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
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

    /// <summary>
    /// The theme's not-found page, unless the address is one the blog had before (docs/specs/old-addresses.md): then a
    /// 301 to where that page is now. Every public page that finds nothing comes here, so an old /author/… or /tag/…
    /// address moves as well as one nothing else claims.
    /// </summary>
    private static async Task<IResult> NotFoundAsync(HttpContext ctx, Theme theme, SiteBlog blog)
    {
        if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
        {
            var db = ctx.RequestServices.GetRequiredService<Db>();
            if (await OldAddressAsync(db, blog, ctx.Request.Path.Value ?? "/", ctx.RequestAborted) is { } now)
                return Results.Redirect(now + ctx.Request.QueryString, permanent: true);
        }
        return await PageAsync(ctx, theme, blog, "not-found", Model(blog, Meta(blog, "Not found", "", blog.Url, "website", null, noindex: true)), 404);
    }

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

    /// <summary>Each feed post's byline, or none when the owner turned bylines off.</summary>
    private static async Task<Dictionary<Guid, Posts.Byline>> FeedBylinesAsync(Posts.Bylines bylines, SiteBlog blog, IReadOnlyList<PublicPost> posts,
        CancellationToken ct) =>
        await bylines.OnAsync(blog.Id, ct) ? await bylines.ForPostsAsync(blog.Id, [.. posts.Select(p => p.Id)], ct) : [];

    private const string DublinCore = "http://purl.org/dc/elements/1.1/";

    private static string Rss(SiteBlog blog, IReadOnlyList<PublicPost> posts, string? hub, Dictionary<Guid, Posts.Byline> bylines) => Write(w =>
    {
        w.WriteStartElement("rss");
        w.WriteAttributeString("version", "2.0");
        w.WriteAttributeString("xmlns", "atom", null, "http://www.w3.org/2005/Atom");
        w.WriteAttributeString("xmlns", "content", null, "http://purl.org/rss/1.0/modules/content/");
        w.WriteAttributeString("xmlns", "dc", null, DublinCore);
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
            // dc:creator, one per writer (RSS's own <author> wants an email address).
            if (bylines.TryGetValue(p.Id, out var by))
                foreach (var name in by.Writers.Select(c => c.Name).Distinct()) w.WriteElementString("dc", "creator", DublinCore, name);
            w.WriteElementString("description", p.Excerpt.Length > 0 ? p.Excerpt : Markdown.ToPlainText(p.BodyMd, 300));
            w.WriteStartElement("content", "encoded", "http://purl.org/rss/1.0/modules/content/");
            w.WriteCData(Markdown.ToHtml(p.BodyMd, new Uri(blog.Url)).Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal));
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();
    });

    private static string Atom(SiteBlog blog, IReadOnlyList<PublicPost> posts, string? hub, Dictionary<Guid, Posts.Byline> bylines) => Write(w =>
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
            // The writers as authors; whoever let it out without writing as a contributor. The blog when bylines are off.
            if (bylines.TryGetValue(p.Id, out var by))
            {
                var writers = by.Writers.Select(c => c.Name).Distinct().ToList();
                foreach (var name in writers) Person(w, ns, "author", name);
                foreach (var name in by.Credits.Where(c => c.Role != "wrote").Select(c => c.Name).Distinct().Except(writers))
                    Person(w, ns, "contributor", name);
            }
            else Person(w, ns, "author", blog.Title);
            if (p.Excerpt.Length > 0) w.WriteElementString("summary", ns, p.Excerpt);
            w.WriteStartElement("content", ns);
            w.WriteAttributeString("type", "html");
            w.WriteString(Markdown.ToHtml(p.BodyMd, new Uri(blog.Url)));
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    private static void Person(XmlWriter w, string ns, string element, string name)
    {
        w.WriteStartElement(element, ns);
        w.WriteElementString("name", ns, name);
        w.WriteEndElement();
    }

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
