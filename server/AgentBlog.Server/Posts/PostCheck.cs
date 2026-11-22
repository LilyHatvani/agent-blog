using System.Text.RegularExpressions;
using AgentBlog.Database;
using AgentBlog.Media;
using AgentKit.Fetch;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AgentBlog.Posts;

/// <summary>The words a check looks at: what readers would get.</summary>
public sealed record CheckText(string Title, string Excerpt, string BodyMd, int TermCount, int SuggestedCount);

/// <summary>One thing the check found. <c>problem</c>: readers would hit something broken. <c>warning</c>: worth a look. <c>note</c>: for information.</summary>
public sealed record Finding(string Check, string Level, string Message, string? Where = null);

public sealed record SimilarPost(Guid Id, string Slug, string Title, string Status, double Similarity);

public sealed record LinkSummary(int External, int Checked, int Broken, int Unknown, int NotChecked);

public sealed record CheckStats(int Words, int ReadingMinutes, int Headings, int Pictures, int Links);

public sealed record CheckResult(bool Ready, int Problems, int Warnings, int Notes, IReadOnlyList<Finding> Findings,
    IReadOnlyList<SimilarPost> Similar, LinkSummary Links, CheckStats Stats);

/// <summary>
/// The pre-flight an agent runs before asking (docs/specs/check.md): title, excerpt, headings, pictures and their alt
/// text, links inside the blog and out, leftovers, and posts it may repeat. Deterministic: rules and counts, no model.
/// Outside links are fetched only when asked (<c>fetchLinks</c>), at most 25 at a time, through the guarded client;
/// what each answered is kept a day per address, so the owner's Inbox shows it without fetching anything.
/// </summary>
public sealed partial class PostChecker(Db db, IHttpClientFactory http, IConfiguration config, TimeProvider clock)
{
    public const int MaxTitle = 70;
    public const int MaxExcerpt = 200;
    public const int MaxLinksPerCheck = 25;
    public static readonly TimeSpan LinkFresh = TimeSpan.FromDays(1);

    private static readonly string[] WeakLinkText = ["here", "click here", "this", "link", "read more", "more", "this link", "this post"];
    private static readonly string[] WeakAlt = ["image", "picture", "photo", "screenshot", "img", "pic", "graphic"];

    [GeneratedRegex(@"\p{L}[\p{L}\p{N}'’-]*")]
    private static partial Regex Word();

    [GeneratedRegex(@"\b(TODO|FIXME|TBD|XXX)\b|lorem ipsum", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"</?[a-zA-Z][a-zA-Z0-9-]*(\s[^<>]*)?/?>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^/media/([0-9a-fA-F-]{36})/([^/?#]+)$")]
    private static partial Regex MediaPath();

    [GeneratedRegex(@"^/posts/([^/?#]+)/?$")]
    private static partial Regex PostPath();

    private sealed record Item(string Url, string Text, bool Image);

    public async Task<CheckResult> CheckAsync(Guid blogId, Guid? postId, CheckText text, bool fetchLinks, CancellationToken ct)
    {
        var f = new List<Finding>();
        var title = text.Title.Trim();
        var excerpt = text.Excerpt.Trim();
        var body = text.BodyMd;

        // Title and excerpt: what search results, feeds and link previews show.
        if (title.Length > MaxTitle)
            f.Add(new("title_length", "warning", $"The title is {title.Length} characters; search results and link previews cut it around {MaxTitle}."));
        if (excerpt.Length == 0)
            f.Add(new("excerpt_missing", "warning", "No excerpt: feeds and link previews show the first words of the post instead."));
        else if (excerpt.Length > MaxExcerpt)
            f.Add(new("excerpt_length", "warning", $"The excerpt is {excerpt.Length} characters; previews cut it around {MaxExcerpt}."));

        // The body: structure, pictures, links, leftovers.
        var doc = Public.Markdown.Parse(body);
        var words = Word().Matches(body).Count;
        if (string.IsNullOrWhiteSpace(body)) f.Add(new("body_empty", "problem", "The post has no text."));
        else if (words < 100) f.Add(new("body_short", "note", $"{words} words: a short post. Fine if that's the point."));

        var headings = doc.Descendants<HeadingBlock>().ToList();
        var previous = 1; // the title is the page's one level-1 heading
        foreach (var h in headings)
        {
            var label = InlineText(h.Inline);
            if (h.Level == 1) f.Add(new("heading_h1", "warning", "The title is already the page's heading: start sections at ##.", Clip(label)));
            else if (h.Level > previous + 1) f.Add(new("heading_skip", "warning", $"A level-{h.Level} heading after a level-{previous} one skips a level (screen readers navigate by them).", Clip(label)));
            if (string.IsNullOrWhiteSpace(label)) f.Add(new("heading_empty", "warning", "An empty heading."));
            previous = h.Level;
        }

        var items = new List<Item>();
        foreach (var link in doc.Descendants<LinkInline>())
            items.Add(new Item(link.Url ?? "", InlineText(link), link.IsImage));
        foreach (var auto in doc.Descendants<AutolinkInline>())
            items.Add(new Item(auto.Url, auto.Url, false));

        // The readers' pipeline turns HTML off, so a tag arrives as literal text (never inside code, which is its own node).
        if (doc.Descendants<LiteralInline>().Any(l => HtmlTag().IsMatch(l.Content.ToString()))
            || doc.Descendants<HtmlBlock>().Any() || doc.Descendants<HtmlInline>().Any())
            f.Add(new("raw_html", "warning", "The post has HTML in it, which readers see as text: write it in Markdown."));
        foreach (Match m in Placeholder().Matches(body))
            f.Add(new("placeholder", "problem", $"\"{m.Value}\" looks like something left to finish.", Around(body, m.Index)));
        if (text.TermCount == 0)
            f.Add(new("no_terms", "note", text.SuggestedCount > 0 ? "No category or tag yet; the owner has suggestions to decide." : "No category or tag: readers browse by them."));

        // Pictures and links that stay on this blog are checked against it; outside ones by fetching.
        var publicHost = Uri.TryCreate(config["Blog:PublicUrl"], UriKind.Absolute, out var pu) ? pu.Host : null;
        string Local(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && publicHost is not null && u.Host == publicHost && u.Scheme is "http" or "https"
            ? u.PathAndQuery : url;
        var mediaRefs = new List<(Item Item, Guid Id, string File)>();
        var postRefs = new List<(Item Item, string Slug)>();
        var external = new List<string>();
        foreach (var it in items)
        {
            var url = Local(it.Url.Trim());
            if (it.Image)
            {
                var alt = it.Text.Trim();
                if (alt.Length == 0) f.Add(new("alt_missing", "problem", "A picture has no description: readers who can't see it get nothing.", Clip(url)));
                else if (WeakAlt.Contains(alt.ToLowerInvariant()) || alt.Equals(Path.GetFileName(url), StringComparison.OrdinalIgnoreCase)
                         || Path.HasExtension(alt) && alt.Length < 60 && !alt.Contains(' ', StringComparison.Ordinal))
                    f.Add(new("alt_weak", "warning", $"\"{Clip(alt)}\" doesn't describe the picture: say what it shows.", Clip(url)));
                else if (alt.Length > 250) f.Add(new("alt_long", "warning", "A picture's description is over 250 characters: a sentence is plenty.", Clip(url)));
            }
            else if (!it.Image && it.Text.Trim().Length == 0)
                f.Add(new("link_text", "warning", "A link with no text: readers can't tell where it goes.", Clip(url)));
            else if (WeakLinkText.Contains(it.Text.Trim().ToLowerInvariant()))
                f.Add(new("link_text", "warning", $"\"{it.Text.Trim()}\" says nothing about where the link goes.", Clip(url)));

            if (url.Length == 0) continue;
            if (!Public.Markdown.SafeUrl(url))
                f.Add(new("link_unsafe", "problem", "A link the blog won't keep (only http, https, mailto and addresses on this blog are kept): readers get a dead link.", Clip(url)));
            else if (MediaPath().Match(url) is { Success: true } mm && Guid.TryParse(mm.Groups[1].Value, out var mid))
                mediaRefs.Add((it, mid, mm.Groups[2].Value));
            else if (PostPath().Match(url) is { Success: true } pm)
                postRefs.Add((it, Uri.UnescapeDataString(pm.Groups[1].Value)));
            else if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https")
            {
                if (abs.Scheme == "http") f.Add(new("link_insecure", "warning", "An http:// link: browsers warn about it; use https:// if the site has it.", Clip(url)));
                if (it.Image) f.Add(new("image_external", "warning", "A picture from another site: it can change or vanish, and isn't under this blog's rules. upload_media_from_url keeps a copy here.", Clip(url)));
                if (!external.Contains(abs.AbsoluteUri)) external.Add(abs.AbsoluteUri);
            }
        }

        var (similar, others) = await ReadBlogAsync(blogId, postId, mediaRefs.Select(m => m.Id).ToArray(), postRefs.Select(p => p.Slug).ToArray(), ct);
        foreach (var (it, id, file) in mediaRefs)
        {
            var m = others.Media.GetValueOrDefault(id);
            if (m is null || m.Filename != file)
                f.Add(new("media_missing", "problem", "A picture that isn't in this blog's media: readers get a missing picture.", Clip(it.Url)));
            else if (m.Status != "ready")
                f.Add(new("media_missing", "problem", m.Status == "trash" ? "A picture that is in the trash: readers get a missing picture." : $"A picture that isn't ready ({m.Status}): confirm_media it first.", Clip(it.Url)));
        }
        foreach (var (it, slug) in postRefs)
        {
            var status = others.Posts.GetValueOrDefault(slug);
            if (status is null or "trash")
                f.Add(new("link_internal", "problem", $"A link to /posts/{slug}, which isn't a post here: readers get \"not found\".", Clip(it.Url)));
            else if (status != "live")
                f.Add(new("link_internal", "warning", $"A link to a post that is {status}, not live yet: readers get \"not found\" until it is.", Clip(it.Url)));
        }

        // Posts this one may repeat: same title, or much of the same text.
        foreach (var s in similar(title, body))
        {
            var same = s.Similarity >= 0.999;
            f.Add(new("similar_post", "warning", same
                ? $"Another post has this title: \"{s.Title}\" ({s.Status})."
                : $"Much of this reads like \"{s.Title}\" ({s.Status}, {Math.Round(s.Similarity * 100)}% alike): is it a repeat, or should they link?", $"/posts/{s.Slug}"));
        }
        var similarList = similar(title, body);

        // Outside links: what they answered (fetched now if asked, else the last day's results).
        var links = await LinksAsync(blogId, external, fetchLinks, f, ct);

        var problems = f.Count(x => x.Level == "problem");
        var warnings = f.Count(x => x.Level == "warning");
        var notes = f.Count(x => x.Level == "note");
        var ordered = f.OrderBy(x => x.Level switch { "problem" => 0, "warning" => 1, _ => 2 }).ToList();
        return new CheckResult(problems == 0, problems, warnings, notes, ordered, similarList, links,
            new CheckStats(words, Math.Max(1, (int)Math.Ceiling(words / 220.0)), headings.Count, items.Count(i => i.Image), items.Count(i => !i.Image)));
    }

    // ------------------------------------------------------------------------------------------ the blog's side

    private sealed record MediaRow(Guid Id, string Status, string Filename);
    private sealed record PostRow(Guid Id, string Slug, string Status, string Title, string BodyMd);
    private sealed record Known(Dictionary<Guid, MediaRow> Media, Dictionary<string, string> Posts);

    private async Task<(Func<string, string, IReadOnlyList<SimilarPost>> Similar, Known Others)> ReadBlogAsync(Guid blogId, Guid? postId,
        Guid[] mediaIds, string[] slugs, CancellationToken ct)
    {
        var (media, posts, recent) = await db.ReadAsync(async tx => (
            await tx.Query<MediaRow>("SELECT id, status, filename FROM media WHERE blog_id = @blogId AND id = ANY (@mediaIds)", new { blogId, mediaIds }),
            await tx.Query<(string Slug, string Status)>("SELECT slug, status FROM post WHERE blog_id = @blogId AND slug = ANY (@slugs)", new { blogId, slugs }),
            // The 1000 most recently changed posts are compared: enough for a blog, bounded for a big one.
            await tx.Query<PostRow>("""
                SELECT p.id, p.slug, p.status, r.title, r.body_md FROM post p JOIN post_revision r ON r.id = p.current_revision_id
                WHERE p.blog_id = @blogId AND p.status <> 'trash' AND p.id IS DISTINCT FROM @postId
                ORDER BY p.updated_at DESC LIMIT 1000
                """, new { blogId, postId })), ct);
        var known = new Known(media.ToDictionary(m => m.Id), posts.ToDictionary(p => p.Slug, p => p.Status));
        var list = recent.ToList();
        IReadOnlyList<SimilarPost> Similar(string title, string body)
        {
            var titleWords = Words(title);
            var shingles = Shingles(body);
            return [.. list
                .Select(p =>
                {
                    var sameTitle = string.Equals(p.Title.Trim(), title, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0;
                    var t = Jaccard(titleWords, Words(p.Title));
                    var b = shingles.Count < 8 ? 0 : Jaccard(shingles, Shingles(p.BodyMd));
                    var score = Math.Max(sameTitle, Math.Max(t >= 0.6 ? t : 0, b >= 0.25 ? b : 0));
                    return new SimilarPost(p.Id, p.Slug, p.Title, p.Status, Math.Round(score, 2));
                })
                .Where(s => s.Similarity > 0)
                .OrderByDescending(s => s.Similarity)
                .Take(3)];
        }
        return (Similar, known);
    }

    private static HashSet<string> Words(string s) =>
        [.. Word().Matches(s.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length >= 3 && !Stop.Contains(w))];

    /// <summary>Three-word runs of the text, lowercased: two posts sharing many of them say much the same thing.</summary>
    private static HashSet<string> Shingles(string s)
    {
        var w = Word().Matches(s.ToLowerInvariant()).Select(m => m.Value).ToArray();
        var set = new HashSet<string>();
        for (var i = 0; i + 2 < w.Length; i++) set.Add($"{w[i]} {w[i + 1]} {w[i + 2]}");
        return set;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count(b.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }

    private static readonly HashSet<string> Stop = ["the", "and", "for", "with", "that", "this", "from", "your", "you", "are", "was", "but", "not", "how", "why", "what", "our", "its", "into", "about"];

    // ----------------------------------------------------------------------------------------- outside links

    private sealed record LinkRow(string Url, string Outcome, int? HttpStatus, string? Detail, DateTime CheckedAt);

    private async Task<LinkSummary> LinksAsync(Guid blogId, List<string> urls, bool fetch, List<Finding> f, CancellationToken ct)
    {
        if (urls.Count == 0) return new LinkSummary(0, 0, 0, 0, 0);
        var now = clock.GetUtcNow();
        var cached = (await db.ReadAsync(tx => tx.Query<LinkRow>(
            "SELECT url, outcome, http_status, detail, checked_at FROM link_check WHERE blog_id = @blogId AND url = ANY (@urls) AND checked_at > @since",
            new { blogId, urls = urls.ToArray(), since = now - LinkFresh }), ct)).ToDictionary(r => r.Url);
        if (fetch)
        {
            var due = urls.Where(u => !cached.ContainsKey(u)).Take(MaxLinksPerCheck).ToList();
            using var gate = new SemaphoreSlim(5);
            var fresh = await Task.WhenAll(due.Select(async u =>
            {
                await gate.WaitAsync(ct);
                try { return await FetchAsync(u, ct); }
                finally { gate.Release(); }
            }));
            await db.OperationalAsync(async tx =>
            {
                foreach (var r in fresh)
                    await tx.Exec("""
                        INSERT INTO link_check (blog_id, url, outcome, http_status, detail, checked_at) VALUES (@blogId, @Url, @Outcome, @HttpStatus, @Detail, @CheckedAt)
                        ON CONFLICT (blog_id, url) DO UPDATE SET outcome = EXCLUDED.outcome, http_status = EXCLUDED.http_status, detail = EXCLUDED.detail, checked_at = EXCLUDED.checked_at
                        """, new { blogId, r.Url, r.Outcome, r.HttpStatus, r.Detail, r.CheckedAt });
                return 0;
            }, ct);
            foreach (var r in fresh) cached[r.Url] = r;
        }
        int broken = 0, unknown = 0;
        foreach (var u in urls)
        {
            if (!cached.TryGetValue(u, out var r)) continue;
            if (r.Outcome == "broken") { broken++; f.Add(new("link_broken", "problem", $"This link is broken ({r.Detail}).", Clip(u))); }
            else if (r.Outcome == "unknown") { unknown++; f.Add(new("link_unknown", "warning", $"Couldn't confirm this link works ({r.Detail}).", Clip(u))); }
        }
        var notChecked = urls.Count(u => !cached.ContainsKey(u));
        if (notChecked > 0)
            f.Add(new("links_not_checked", "note", fetch
                ? $"{notChecked} outside link{(notChecked == 1 ? "" : "s")} not checked: at most {MaxLinksPerCheck} per check. Run it again for the rest."
                : $"{notChecked} outside link{(notChecked == 1 ? "" : "s")} not checked yet."));
        return new LinkSummary(urls.Count, urls.Count - notChecked, broken, unknown, notChecked);
    }

    /// <summary>
    /// One outside link, as a reader's browser would find it: HEAD, then GET when a site doesn't take HEAD; redirects
    /// followed through the guard. 404 and 410 are broken; any other answer that isn't 2xx or 3xx, a timeout, or an
    /// address the guard refuses, is "couldn't confirm" (sites refuse robots, rate-limit, or sit behind a login).
    /// </summary>
    private async Task<LinkRow> FetchAsync(string url, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        try
        {
            using var client = http.CreateClient(AgentBlog.Media.SafeFetch.HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(8);
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            head.Headers.UserAgent.ParseAdd("AgentBlog-LinkCheck/1.0");
            using var res = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)res.StatusCode;
            if (status is 405 or 501 or 403)
            {
                using var get = new HttpRequestMessage(HttpMethod.Get, url);
                get.Headers.UserAgent.ParseAdd("AgentBlog-LinkCheck/1.0");
                using var res2 = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
                status = (int)res2.StatusCode;
            }
            return status switch
            {
                >= 200 and < 400 => new LinkRow(url, "ok", status, null, now),
                404 or 410 => new LinkRow(url, "broken", status, $"the site answers {status}", now),
                _ => new LinkRow(url, "unknown", status, $"the site answers {status}", now),
            };
        }
        catch (Exception e) when (GuardedHttp.Blocked(e) is { } blocked)
        {
            return new LinkRow(url, "broken", null, "not on the public internet: readers can't reach it", now);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new LinkRow(url, "unknown", null, e is TaskCanceledException ? "no answer within 8 seconds" : "no connection", now);
        }
    }

    // -------------------------------------------------------------------------------------------- the answer

    /// <summary>The check as the tool, the app and the Inbox send it.</summary>
    public static object Json(CheckResult r) => new
    {
        ready = r.Ready,
        summary = r.Problems + r.Warnings == 0 ? "Nothing to fix." : string.Join(", ", new[]
        {
            r.Problems > 0 ? $"{r.Problems} problem{(r.Problems == 1 ? "" : "s")}" : null,
            r.Warnings > 0 ? $"{r.Warnings} warning{(r.Warnings == 1 ? "" : "s")}" : null,
        }.Where(x => x is not null)),
        problems = r.Problems, warnings = r.Warnings, notes = r.Notes,
        findings = r.Findings.Select(x => new { check = x.Check, level = x.Level, message = x.Message, where = x.Where }),
        similar_posts = r.Similar.Select(s => new { id = s.Id, slug = s.Slug, title = s.Title, status = s.Status, similarity = s.Similarity }),
        links = new { outside = r.Links.External, @checked = r.Links.Checked, broken = r.Links.Broken, unconfirmed = r.Links.Unknown, not_checked = r.Links.NotChecked },
        stats = new { words = r.Stats.Words, reading_minutes = r.Stats.ReadingMinutes, headings = r.Stats.Headings, pictures = r.Stats.Pictures, links = r.Stats.Links },
    };

    /// <summary>A post as it stands, with any fields an agent is about to change put over it (a dry run).</summary>
    public static CheckText Over(PostView p, string? title = null, string? excerpt = null, string? bodyMd = null) =>
        new(title ?? p.Title, excerpt ?? p.Excerpt, bodyMd ?? p.BodyMd ?? "", p.Terms.Count, p.Suggested.Count);

    // ----------------------------------------------------------------------------------------------- helpers

    private static string InlineText(ContainerInline? inline) =>
        inline is null ? "" : string.Concat(inline.Descendants<LiteralInline>().Select(l => l.Content.ToString())).Trim();

    private static string Clip(string s) => s.Length <= 120 ? s : s[..119] + "…";

    private static string Around(string body, int index)
    {
        var start = Math.Max(0, index - 30);
        var len = Math.Min(body.Length - start, 80);
        return Clip(body.Substring(start, len).ReplaceLineEndings(" ").Trim());
    }
}
