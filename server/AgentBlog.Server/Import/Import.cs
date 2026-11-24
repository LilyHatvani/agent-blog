using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentBlog.Auth;
using AgentBlog.Database;
using AgentBlog.Media;
using AgentBlog.Posts;

namespace AgentBlog.Import;

/// <summary>An import as the owner sees it: where from, how far, what happened.</summary>
public sealed record ImportJob(Guid Id, Guid BlogId, string Source, string SourceUrl, string? ProfileBase, string AuthorMap, string State, string Counts,
    string Log, string? Error, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt, bool UpdateChanged);

/// <summary>
/// Imports (docs/specs/import.md): the owner starts one, a worker runs it in the background, and its counts and log are
/// saved as it goes. One at a time per blog. A run interrupted (a restart) is taken up again after a few minutes, and an
/// import run again skips what is already here, so nothing is ever brought in twice.
/// </summary>
public sealed class ImportJobs(Db db, TimeProvider clock)
{
    public const string HttpClientName = "import";
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);
    public const int MaxLog = 400;

    private const string Select = """
        SELECT id, blog_id, source, source_url, profile_base, author_map::text AS author_map, state, counts::text AS counts, log::text AS log, error, created_at, started_at, finished_at, update_changed
        FROM import_job
        """;

    /// <summary>An https address with a host and nothing hidden in it, without a trailing slash; or why not.</summary>
    public static (string? Url, string? Error) CleanUrl(string? given, string what)
    {
        var url = (given ?? "").Trim().TrimEnd('/');
        if (url.Length == 0 || url.Length > 300 || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps
            || !u.Host.Contains('.') || u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0)
            return (null, $"{what} is a full https:// address, such as https://serverapi-staging.up.railway.app.");
        return (url, null);
    }

    public async Task<(ImportJob? Job, string? Error)> StartAsync(Actor owner, string? sourceUrl, string? profileBase,
        IReadOnlyDictionary<string, Guid>? authorMap, CancellationToken ct, bool updateChanged = false)
    {
        if (!owner.IsOwner) return (null, "Only the owner imports.");
        var (source, sourceError) = CleanUrl(sourceUrl, "The Echo of Dreams address");
        if (sourceError is not null) return (null, sourceError);
        string? profiles = null;
        if (!string.IsNullOrWhiteSpace(profileBase))
        {
            (profiles, var profileError) = CleanUrl(profileBase, "The profiles address");
            if (profileError is not null) return (null, profileError);
        }
        // "This EoD author is an account already here" (at least "this author is me"): only accounts on this blog.
        var map = new Dictionary<string, Guid>();
        foreach (var (key, account) in authorMap ?? new Dictionary<string, Guid>())
        {
            var k = key.Trim();
            if (k.Length is < 1 or > 200) return (null, "An author map's key is an EoD author's slug or name, 1–200 characters.");
            if (!await db.ReadAsync(tx => tx.One<bool>("SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @BlogId AND account_id = @account)",
                    new { owner.BlogId, account }), ct))
                return (null, "An author map can only name accounts already on this blog.");
            map[k] = account;
        }
        var id = Guid.CreateVersion7();
        var now = clock.GetUtcNow();
        var started = await db.WriteAsync(async tx =>
        {
            if (await tx.One<bool>("SELECT EXISTS (SELECT 1 FROM import_job WHERE blog_id = @BlogId AND state IN ('queued', 'running'))", owner))
                return false;
            await tx.Exec("""
                INSERT INTO import_job (id, blog_id, source, source_url, profile_base, author_map, update_changed, created_by, created_at)
                VALUES (@id, @BlogId, 'eod', @source, @profiles, @map::jsonb, @updateChanged, @AccountId, @now)
                """, new { id, owner.BlogId, source, profiles, map = JsonSerializer.Serialize(map), updateChanged, owner.AccountId, now });
            await tx.Event(new EventRow(owner.BlogId, "import", id, "import_started", owner.Value, new { source = "eod", url = source }), now);
            return true;
        }, ct);
        if (!started) return (null, "An import is already running on this blog: wait for it to finish.");
        return ((await ListAsync(owner.BlogId, ct)).First(j => j.Id == id), null);
    }

    public Task<IReadOnlyList<ImportJob>> ListAsync(Guid blogId, CancellationToken ct) => db.ReadAsync<IReadOnlyList<ImportJob>>(async tx =>
        [.. await tx.Query<ImportJob>($"{Select} WHERE blog_id = @blogId ORDER BY created_at DESC LIMIT 10", new { blogId })], ct);

    /// <summary>The next import to run: a queued one, or one whose worker stopped answering (a restart mid-run).</summary>
    public Task<ImportJob?> ClaimAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.OperationalAsync(tx => tx.One<ImportJob>($"""
            UPDATE import_job SET state = 'running', started_at = coalesce(started_at, @now), heartbeat_at = @now
            WHERE id = (SELECT id FROM import_job
                        WHERE state = 'queued' OR (state = 'running' AND heartbeat_at < @stale)
                        ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING id, blog_id, source, source_url, profile_base, author_map::text AS author_map, state, counts::text AS counts, log::text AS log, error, created_at, started_at, finished_at, update_changed
            """, new { now, stale = now - Stale }), ct);
    }

    /// <summary>Saves how far it got (and that it is still alive). Progress, not a state change: no event.</summary>
    public Task SaveAsync(Guid id, ImportRun run, CancellationToken ct) => db.OperationalAsync(async tx =>
    {
        await tx.Exec("UPDATE import_job SET counts = @counts::jsonb, log = @log::jsonb, heartbeat_at = @now WHERE id = @id",
            new { id, counts = run.CountsJson(), log = run.LogJson(), now = clock.GetUtcNow() });
        return 0;
    }, ct);

    public Task FinishAsync(ImportJob job, ImportRun run, string? error, CancellationToken ct) => db.WriteAsync(async tx =>
    {
        var now = clock.GetUtcNow();
        await tx.Exec("""
            UPDATE import_job SET state = @state, counts = @counts::jsonb, log = @log::jsonb, error = @error, finished_at = @now, heartbeat_at = @now
            WHERE id = @Id
            """, new { job.Id, state = error is null ? "done" : "failed", counts = run.CountsJson(), log = run.LogJson(), error, now });
        await tx.Event(new EventRow(job.BlogId, "import", job.Id, error is null ? "import_finished" : "import_failed", Actors.System,
            new { counts = JsonNode.Parse(run.CountsJson()), error }), now);
        return 0;
    }, ct);
}

/// <summary>An import's counts and log as it runs: what the owner reads in the app.</summary>
public sealed class ImportRun
{
    private readonly Dictionary<string, int> _counts;
    private readonly List<Dictionary<string, string>> _log;

    public ImportRun(ImportJob job)
    {
        _counts = JsonSerializer.Deserialize<Dictionary<string, int>>(job.Counts) ?? [];
        _log = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(job.Log) ?? [];
    }

    public void Count(string what, int by = 1) => _counts[what] = _counts.GetValueOrDefault(what) + by;

    public int this[string what] => _counts.GetValueOrDefault(what);

    /// <summary>A line for the owner: <c>info</c>, <c>warning</c> (something left out, with what to do) or <c>error</c>.</summary>
    public void Note(string level, string text)
    {
        _log.Add(new() { ["level"] = level, ["text"] = text.Length > 500 ? text[..500] + "…" : text });
        if (_log.Count > ImportJobs.MaxLog) _log.RemoveRange(0, _log.Count - ImportJobs.MaxLog);
    }

    public string CountsJson() => JsonSerializer.Serialize(_counts);
    public string LogJson() => JsonSerializer.Serialize(_log);
}

/// <summary>
/// Echo of Dreams' public blog API, as an anonymous reader sees it (docs/specs/import.md § The source): the published
/// posts, a page at a time; each post's Markdown; and its gallery pictures, resolved for a signed-out reader (the best
/// public variant of each, and only the pictures any reader may see). A 429 waits as asked and tries again.
/// </summary>
public sealed class EodApi(HttpClient http, string baseUrl)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int PageSize = 50;
    public const int MaxPosts = 5000;

    public sealed record Author(string? Slug, string? DisplayName);
    /// <summary>A source category: its address and name, its id, and its parent's id when it has one (the hierarchy).</summary>
    public sealed record Category(string Slug, string Name, int? Id = null, int? ParentId = null);
    public sealed record Summary(Guid Uuid, string Slug, string Title, DateTime? PublishedAt);
    public sealed record ListPage(List<Summary> Items, int Page, int PageSize, int Total);
    public sealed record Post(Guid Uuid, string Slug, string Title, string? Excerpt, string Content, string? CoverImageUrl, string? SeoDescription,
        DateTime? PublishedAt, DateTime UpdatedAt, List<string>? Tags, Author? Author, Category? Category, string? FeedId = null);
    public sealed record Resolved(int Tree, string Ref, string ThumbUrl);
    private sealed record ResolveAnswer(string? Admission, List<Resolved>? Pictures);

    public async Task<List<Summary>> ListAsync(CancellationToken ct)
    {
        var all = new List<Summary>();
        for (var page = 1; all.Count < MaxPosts; page++)
        {
            var answer = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/blog/posts?page={page}&pageSize={PageSize}"), ct);
            answer.EnsureSuccessStatusCode();
            var list = await answer.Content.ReadFromJsonAsync<ListPage>(Json, ct) ?? throw new InvalidDataException("The post list was empty.");
            all.AddRange(list.Items);
            if (list.Items.Count == 0 || all.Count >= list.Total) break;
        }
        return all;
    }

    /// <summary>The source's categories, each with its id and parent id, so a child category can name its parent.</summary>
    public async Task<List<Category>> CategoriesAsync(CancellationToken ct)
    {
        var answer = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/blog/categories"), ct);
        answer.EnsureSuccessStatusCode();
        return await answer.Content.ReadFromJsonAsync<List<Category>>(Json, ct) ?? [];
    }

    public async Task<Post?> PostAsync(string slug, CancellationToken ct)
    {
        var answer = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/blog/posts/{Uri.EscapeDataString(slug)}"), ct);
        if (answer.StatusCode == HttpStatusCode.NotFound) return null;
        answer.EnsureSuccessStatusCode();
        return await answer.Content.ReadFromJsonAsync<Post>(Json, ct);
    }

    /// <summary>EoD refuses a resolve call with more than this many refs (too_many_refs), so they go in chunks.</summary>
    public const int MaxRefsPerCall = 100;

    /// <summary>Each picture reference a signed-out reader may see, with its address now. Refs left out are refused or gone.</summary>
    public async Task<List<Resolved>> ResolveAsync(string? owner, IReadOnlyList<string> refs, CancellationToken ct)
    {
        var all = new List<Resolved>();
        for (var at = 0; at < refs.Count; at += MaxRefsPerCall)
        {
            var chunk = refs.Skip(at).Take(MaxRefsPerCall).ToList();
            var answer = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/pictures/resolve")
            {
                Content = JsonContent.Create(new { trees = new[] { new { owner, refs = chunk } } }),
            }, ct);
            answer.EnsureSuccessStatusCode();
            all.AddRange((await answer.Content.ReadFromJsonAsync<ResolveAnswer>(Json, ct))?.Pictures ?? []);
        }
        return all;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var req = request();
            req.Headers.UserAgent.ParseAdd("AgentBlog/1 (import)");
            var res = await http.SendAsync(req, ct);
            if (res.StatusCode != HttpStatusCode.TooManyRequests || attempt >= 4) return res;
            var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(20);
            res.Dispose();
            await Task.Delay(wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait, ct);
        }
    }
}

/// <summary>
/// Brings Echo of Dreams' blog over (docs/specs/import.md): every published post, keeping its uuid (so EoD's comments stay
/// attached), slug, dates, tags and category; its pictures copied into this blog's bucket and their links rewritten;
/// each author as a writer here, under their EoD address and with their EoD profile as their link; and an old address
/// for anything whose address changed on the way. History starts at the import.
/// </summary>
public sealed partial class EodImporter(IHttpClientFactory http, Db db, PostService posts, MediaService media, ImportJobs jobs, TimeProvider clock)
{
    public const string Source = "eod";

    [GeneratedRegex(@"!\[(?<alt>[^\]\n]*)\]\((?<ref>gi?://[^)\s]+)(?<title>\s+""[^""]*"")?\)")]
    private static partial Regex GalleryPicture();

    public async Task RunAsync(ImportJob job, ImportRun run, CancellationToken ct)
    {
        var api = new EodApi(http.CreateClient(ImportJobs.HttpClientName), job.SourceUrl);
        var owner = await db.ReadAsync(tx => tx.One<Guid>("SELECT account_id FROM blog_member WHERE blog_id = @BlogId AND role = 'owner'", job), ct);
        var summaries = await api.ListAsync(ct);
        // The source's category hierarchy, read once: a child category names its parent (kept as a description note,
        // since this blog's categories are flat — docs/specs/import.md § Category parents).
        var categories = await api.CategoriesAsync(ct);
        run.Note("info", $"Echo of Dreams lists {summaries.Count} published post{(summaries.Count == 1 ? "" : "s")}.");
        // Oldest first: when two slugs meet, the older post keeps its address.
        foreach (var s in summaries.OrderBy(s => s.PublishedAt ?? DateTime.MaxValue).ThenBy(s => s.Uuid))
        {
            ct.ThrowIfCancellationRequested();
            run.Count("seen");
            // Where the uuid lives, checked before anything is fetched or made: this blog's post is skipped, and one
            // on another blog fails with the reason without a fetch, a writer or a picture (ImportAsync's
            // in-transaction check stays as a backstop against a race).
            var home = await db.ReadAsync(tx => tx.One<Guid?>("SELECT blog_id FROM post WHERE id = @Uuid", new { s.Uuid }), ct);
            if (home == job.BlogId)
            {
                // "Update changed posts": a source edit comes into a post already imported from here, as a new revision.
                if (job.UpdateChanged)
                {
                    try
                    {
                        await UpdateOneAsync(api, job, run, s, owner, categories, ct);
                    }
                    catch (Exception e) when (e is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        run.Count("failed");
                        run.Note("error", $"“{s.Title}” ({s.Slug}) wasn't updated: {e.Message} Run the import again to try it once more.");
                    }
                    await jobs.SaveAsync(job.Id, run, ct);
                }
                else run.Count("already_here");
                continue;
            }
            if (home is not null)
            {
                run.Count("failed");
                run.Note("error", $"“{s.Title}” ({s.Slug}) didn't come over: its id is already on another blog on this instance; a post lives on one blog only.");
            }
            else
            {
                try
                {
                    await ImportOneAsync(api, job, run, s, owner, categories, ct);
                }
                catch (Exception e) when (e is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    run.Count("failed");
                    run.Note("error", $"“{s.Title}” ({s.Slug}) didn't come over: {e.Message} Run the import again to try it once more.");
                }
            }
            await jobs.SaveAsync(job.Id, run, ct);
        }
    }

    private async Task ImportOneAsync(EodApi api, ImportJob job, ImportRun run, EodApi.Summary s, Guid owner,
        IReadOnlyList<EodApi.Category> categories, CancellationToken ct)
    {
        if (await api.PostAsync(s.Slug, ct) is not { } post)
        {
            run.Count("failed");
            run.Note("warning", $"“{s.Title}” ({s.Slug}) is listed but its page answers not found; it was left out.");
            return;
        }
        var author = await AuthorAsync(job, run, post.Author, owner, ct);
        var body = await PicturesAsync(api, job, run, post, author, ct);
        var excerpt = (post.Excerpt is { Length: > 0 } e ? e : post.SeoDescription ?? "").Trim();
        var published = post.PublishedAt ?? post.UpdatedAt;
        // The source's feed id, so a feed reader doesn't show the post again after a move: the field the source gives,
        // else {source host}/{slug}. A post written here has none and keeps its own id (docs/specs/public-site.md).
        var feedId = post.FeedId is { Length: > 0 } f ? f : $"{new Uri(job.SourceUrl).Host}/{post.Slug}";
        var result = await posts.ImportAsync(job.BlogId, post.Uuid, post.Slug, post.Title, excerpt, body,
            post.Category is { } c ? [CategorySeed(c, categories)] : [], post.Tags ?? [], DateTime.SpecifyKind(published, DateTimeKind.Utc),
            DateTime.SpecifyKind(post.UpdatedAt, DateTimeKind.Utc), author, Source, feedId, ct);
        if (result.AlreadyHere)
        {
            run.Count("already_here");
            return;
        }
        if (result.Error is { } error)
        {
            run.Count("failed");
            run.Note("error", $"“{post.Title}” ({post.Slug}) didn't come over: {error}");
            return;
        }
        run.Count("imported");
        NoteDropped(run, post.Title, result.Dropped);
        // EoD's post address was /{slug}: the site moves a live post's /{slug} by itself; a slug that changed needs a row.
        if (result.Slug != post.Slug)
        {
            await OldAddressAsync(job.BlogId, "/" + post.Slug, "/posts/" + result.Slug, ct);
            run.Note("info", $"“{post.Title}” is at /posts/{result.Slug} (its slug {post.Slug} was taken); /{post.Slug} moves there.");
        }
        // EoD's term addresses: /category/{slug} and /tag/{the name as written, escaped}. One that landed at a
        // different address here moves its old one; a category keeps the source's own slug when it is an address here.
        if (result.Post is { } imported)
        {
            if (post.Category is { } cat && cat.Slug.Length > 0
                && imported.Terms.FirstOrDefault(t => t.Kind == Taxonomy.Category) is { } finalCat && finalCat.Slug != cat.Slug)
                await OldAddressAsync(job.BlogId, "/category/" + Uri.EscapeDataString(cat.Slug), "/category/" + finalCat.Slug, ct);
            foreach (var tag in post.Tags ?? [])
                if (Taxonomy.Slug(tag) is { } tagSlug && Uri.EscapeDataString(tag) != tagSlug)
                    await OldAddressAsync(job.BlogId, "/tag/" + Uri.EscapeDataString(tag), "/tag/" + tagSlug, ct);
        }
    }

    /// <summary>What an imported post remembers of its source, read to decide whether a re-import update applies.</summary>
    private sealed record ImportedPostRow(string? ImportSource, DateTime? ImportUpdatedAt, Guid? ImportRevisionId, Guid CurrentRevisionId);

    /// <summary>
    /// "Update changed posts" (docs/specs/import.md § Running it again): a post already imported from this source, whose
    /// source <c>updatedAt</c> is newer than the copy here, gets the source's changed words as a new revision. A post
    /// edited here after the import is skipped with a log line naming it; an unchanged one is left alone. The detail is
    /// fetched only after the local-edit check, so a post someone edited here costs no fetch.
    /// </summary>
    private async Task UpdateOneAsync(EodApi api, ImportJob job, ImportRun run, EodApi.Summary s, Guid owner,
        IReadOnlyList<EodApi.Category> categories, CancellationToken ct)
    {
        var row = await db.ReadAsync(tx => tx.One<ImportedPostRow?>("""
            SELECT import_source, import_updated_at, import_revision_id, current_revision_id
            FROM post WHERE id = @Uuid AND blog_id = @BlogId
            """, new { s.Uuid, job.BlogId }), ct);
        // Not imported from this source (or imported before updates were tracked): nothing to update.
        if (row is null || row.ImportSource != Source || row.ImportUpdatedAt is null)
        {
            run.Count("already_here");
            return;
        }
        if (row.ImportRevisionId != row.CurrentRevisionId)
        {
            run.Count("skipped_local");
            run.Note("warning", $"“{s.Title}” ({s.Slug}) was edited here after the import; the source's change was left alone.");
            return;
        }
        if (await api.PostAsync(s.Slug, ct) is not { } post)
        {
            run.Count("already_here");
            return;
        }
        var sourceUpdated = DateTime.SpecifyKind(post.UpdatedAt, DateTimeKind.Utc);
        if (sourceUpdated <= row.ImportUpdatedAt.Value)
        {
            run.Count("unchanged");
            return;
        }
        var author = await AuthorAsync(job, run, post.Author, owner, ct);
        var body = await PicturesAsync(api, job, run, post, author, ct);
        var excerpt = (post.Excerpt is { Length: > 0 } e ? e : post.SeoDescription ?? "").Trim();
        var result = await posts.ImportUpdateAsync(job.BlogId, post.Uuid, post.Title, excerpt, body,
            post.Category is { } c ? [CategorySeed(c, categories)] : [], post.Tags ?? [], sourceUpdated, author, Source, ct);
        if (result.Error == "edited_here")
        {
            run.Count("skipped_local");
            run.Note("warning", $"“{s.Title}” ({s.Slug}) was edited here after the import; the source's change was left alone.");
            return;
        }
        if (result.Error is { } error)
        {
            run.Count("failed");
            run.Note("error", $"“{post.Title}” ({post.Slug}) wasn't updated: {error}");
            return;
        }
        run.Count("updated");
        run.Note("info", $"“{post.Title}” ({post.Slug}) was updated from the source (revision {result.Post!.RevisionNumber}).");
        NoteDropped(run, post.Title, result.Dropped);
    }

    /// <summary>The terms an import left off a post, named in the log with the reason (the post still came over).</summary>
    private static void NoteDropped(ImportRun run, string title, IReadOnlyList<(string Kind, string Name, string Why)>? dropped)
    {
        foreach (var (kind, name, why) in dropped ?? [])
        {
            run.Count("terms_dropped");
            run.Note("warning", why switch
            {
                "duplicate" => $"The {kind} “{name}” was left off “{title}”: another {kind} on the post already has its address.",
                "over-cap" => $"The {kind} “{name}” was left off “{title}”: the post already carries the most {kind}s it may.",
                _ => $"The {kind} “{name}” was left off “{title}”: its name has no A–Z/0–9 address here.",
            });
        }
    }

    /// <summary>
    /// A source category as a term seed. This blog's categories are flat (the term table has no parent), so a child
    /// keeps its parent's name as a description note rather than a parent link; the parent itself is an ordinary
    /// category. The parent id comes from the source's categories endpoint (or the category object itself).
    /// </summary>
    private static TermSeed CategorySeed(EodApi.Category c, IReadOnlyList<EodApi.Category> all)
    {
        var row = all.FirstOrDefault(x => c.Id is { } id && x.Id == id) ?? all.FirstOrDefault(x => x.Slug == c.Slug);
        var parentId = row?.ParentId ?? c.ParentId;
        var parent = parentId is { } pid ? all.FirstOrDefault(x => x.Id == pid) : null;
        return new TermSeed(c.Name, c.Slug, parent is { } p ? $"Under “{p.Name}” on the source blog." : null);
    }

    /// <summary>
    /// The account an EoD author writes under here, made the first time and found every time after: a writer on the blog,
    /// at their EoD author address when it's free, with their EoD profile as their link. A post with no author is the
    /// owner's. The owner's map ("this author is me") comes first: that author is an account already here, and their
    /// EoD author address moves to their page.
    /// </summary>
    private async Task<Guid> AuthorAsync(ImportJob job, ImportRun run, EodApi.Author? who, Guid owner, CancellationToken ct)
    {
        var key = (who?.Slug ?? who?.DisplayName)?.Trim();
        if (string.IsNullOrEmpty(key)) return owner;
        var map = JsonSerializer.Deserialize<Dictionary<string, Guid>>(job.AuthorMap) ?? [];
        // The map answers to the author's slug first, then their display name: a map keyed by name still lands
        // an author who has a slug.
        string? matched = null;
        var mapped = Guid.Empty;
        if (who?.Slug?.Trim() is { Length: > 0 } slugKey && map.TryGetValue(slugKey, out var bySlug)) { matched = slugKey; mapped = bySlug; }
        else if (who?.DisplayName?.Trim() is { Length: > 0 } nameKey && map.TryGetValue(nameKey, out var byName)) { matched = nameKey; mapped = byName; }
        if (matched is not null)
        {
            // Remembered for later runs too, so a run without the map still knows who that author is here.
            await db.WriteAsync(async tx =>
            {
                await tx.Exec("""
                    INSERT INTO import_author (blog_id, source, source_key, account_id) VALUES (@BlogId, @Source, @key, @mapped)
                    ON CONFLICT (blog_id, source, source_key) DO UPDATE SET account_id = excluded.account_id
                    """, new { job.BlogId, Source, key = matched, mapped });
                return 0;
            }, ct);
            var hereSlug = await db.ReadAsync(tx => tx.One<string?>("SELECT slug FROM author WHERE blog_id = @BlogId AND account_id = @mapped",
                new { job.BlogId, mapped }), ct);
            if (who!.Slug is { Length: > 0 } old && hereSlug is { Length: > 0 } here && here != old)
                await OldAddressAsync(job.BlogId, "/author/" + old, "/author/" + here, ct);
            return mapped;
        }
        var name = (who!.DisplayName ?? who.Slug)!.Trim();
        if (name.Length > 100) name = name[..100];
        var slug = who.Slug is { Length: > 0 } s ? PostService.Slugify(s) : null;
        var profile = job.ProfileBase is { } p && who.Slug is { Length: > 0 } ps ? $"{p}/creator/{Uri.EscapeDataString(ps)}" : null;
        var (account, made, ours) = await db.WriteAsync(async tx =>
        {
            if (await tx.One<Guid?>("SELECT account_id FROM import_author WHERE blog_id = @BlogId AND source = @Source AND source_key = @key",
                    new { job.BlogId, Source, key }) is { } known)
                return (known, false, (string?)null);
            var now = clock.GetUtcNow();
            var id = Guid.CreateVersion7();
            await tx.Exec("INSERT INTO account (id, display_name, created_at) VALUES (@id, @name, @now)", new { id, name, now });
            await tx.Event(new EventRow(null, "account", id, "account_created", Actors.System, new { via = "import", source = Source }), now);
            // A writer, as every EoD author was; the trigger gives them an author address from their name.
            await tx.Exec("INSERT INTO blog_member (blog_id, account_id, role, created_at) VALUES (@BlogId, @id, 'writer', @now)",
                new { job.BlogId, id, now });
            if (slug is not null)
                await tx.Exec("""
                    UPDATE author SET slug = @slug WHERE blog_id = @BlogId AND account_id = @id
                      AND NOT EXISTS (SELECT 1 FROM author o WHERE o.blog_id = @BlogId AND o.slug = @slug AND o.account_id <> @id)
                    """, new { job.BlogId, id, slug });
            if (profile is not null)
                await tx.Exec("UPDATE author SET profile_url = @profile WHERE blog_id = @BlogId AND account_id = @id", new { job.BlogId, id, profile });
            await tx.Exec("INSERT INTO import_author (blog_id, source, source_key, account_id) VALUES (@BlogId, @Source, @key, @id)",
                new { job.BlogId, Source, key, id });
            await tx.Event(new EventRow(job.BlogId, "account", id, "member_imported", Actors.System, new { source = Source, role = "writer" }), now);
            var mine = await tx.One<string>("SELECT slug FROM author WHERE blog_id = @BlogId AND account_id = @id", new { job.BlogId, id });
            return (id, true, mine);
        }, ct);
        if (made)
        {
            run.Count("authors");
            run.Note("info", $"{name} is a writer here now, at /author/{ours}. Invite them from People to sign in as themselves.");
            // Their EoD author page was /author/{eod slug}: when theirs here differs, the old one moves.
            if (who.Slug is { Length: > 0 } old && ours != old)
                await OldAddressAsync(job.BlogId, "/author/" + old, "/author/" + ours, ct);
        }
        return account;
    }

    /// <summary>
    /// The post's gallery pictures (and its cover, first in the body) copied into this blog's bucket, their links
    /// rewritten to the copies. One a signed-out reader may not see, or that fails to copy, becomes its description in
    /// italics, and the log says which.
    /// </summary>
    private async Task<string> PicturesAsync(EodApi api, ImportJob job, ImportRun run, EodApi.Post post, Guid author, CancellationToken ct)
    {
        var body = post.Content;
        var cover = post.CoverImageUrl is { Length: > 0 } c && !body.Contains(c, StringComparison.Ordinal) ? c : null;
        var refs = GalleryPicture().Matches(body).Select(m => m.Groups["ref"].Value).ToList();
        if (cover is not null && (cover.StartsWith("g://", StringComparison.Ordinal) || cover.StartsWith("gi://", StringComparison.Ordinal))) refs.Add(cover);
        refs = [.. refs.Distinct()];
        var resolved = refs.Count == 0 ? [] : (await api.ResolveAsync(post.Author?.Slug, refs, ct)).ToDictionary(r => r.Ref, r => r.ThumbUrl);
        var actor = Actor.Person(job.BlogId, author, Roles.Writer);
        var copied = new Dictionary<string, string?>();

        async Task<string?> CopyAsync(string source, string alt)
        {
            if (copied.TryGetValue(source, out var done)) return done;
            var url = resolved.TryGetValue(source, out var r) ? r : source.StartsWith("https://", StringComparison.Ordinal) ? source : null;
            string? path = null;
            if (url is null)
            {
                run.Count("pictures_missing");
                run.Note("warning", $"A picture in “{post.Title}” ({source}) isn't public on Echo of Dreams; its description stands in for it.");
            }
            else if (await media.FromUrlAsync(actor, url, alt, null, ct) is { Media: { } m })
            {
                run.Count("pictures");
                path = m.Path;
                // A post can carry a hundred pictures at twenty seconds each: save the heartbeat every ten, so a
                // long copy never looks stale (and the owner sees the progress while it runs).
                if (run["pictures"] % 10 == 0) await jobs.SaveAsync(job.Id, run, ct);
            }
            else
            {
                run.Count("pictures_missing");
                run.Note("warning", $"A picture in “{post.Title}” ({source}) didn't copy; its description stands in for it.");
            }
            return copied[source] = path;
        }

        var parts = new List<string>();
        var at = 0;
        foreach (Match m in GalleryPicture().Matches(body))
        {
            parts.Add(body[at..m.Index]);
            var alt = m.Groups["alt"].Value;
            var path = await CopyAsync(m.Groups["ref"].Value, alt);
            parts.Add(path is not null ? $"![{alt}]({path}{m.Groups["title"].Value})" : alt.Length > 0 ? $"*{alt}*" : "");
            at = m.Index + m.Length;
        }
        parts.Add(body[at..]);
        body = string.Concat(parts);
        if (cover is not null && await CopyAsync(cover, post.Title) is { } coverPath)
            body = $"![{post.Title.Replace("]", "")}]({coverPath})\n\n{body}";
        return body;
    }

    private Task OldAddressAsync(Guid blogId, string path, string target, CancellationToken ct) => db.WriteAsync(async tx =>
    {
        if (path == target || path.Length > 500 || target.Length > 500) return 0;
        return await tx.Exec("""
            INSERT INTO old_address (blog_id, path, target) VALUES (@blogId, @path, @target) ON CONFLICT (blog_id, path) DO NOTHING
            """, new { blogId, path, target });
    }, ct);
}

/// <summary>Runs imports in the background (<c>Blog__Import__IntervalSeconds</c>, 5; 0 turns it off, as the tests do).</summary>
public sealed class ImportWorker(ImportJobs jobs, EodImporter importer, IConfiguration config, ILogger<ImportWorker> log) : BackgroundService
{
    /// <summary>One pass: claims an import and runs it to the end. Returns whether there was one.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        if (await jobs.ClaimAsync(ct) is not { } job) return false;
        var run = new ImportRun(job);
        try
        {
            await importer.RunAsync(job, run, ct);
            run.Note("info", $"Done: {run["imported"]} imported, {run["already_here"]} already here, {run["failed"]} failed; "
                + $"{run["pictures"]} pictures copied, {run["pictures_missing"]} left out; {run["authors"]} new writers."
                + (job.UpdateChanged ? $" Updated: {run["updated"]} changed, {run["unchanged"]} unchanged, {run["skipped_local"]} edited here (skipped)." : ""));
            await jobs.FinishAsync(job, run, null, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogError(e, "Import {Job} failed", job.Id);
            var why = e is HttpRequestException h
                ? $"Echo of Dreams didn't answer as expected ({(h.StatusCode is { } code ? $"{(int)code}" : h.Message)}). Check the address and run it again."
                : $"The import stopped: {e.Message}";
            run.Note("error", why);
            await jobs.FinishAsync(job, run, why, CancellationToken.None);
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = config.GetValue("Blog:Import:IntervalSeconds", 5);
        if (seconds <= 0) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await RunOnceAsync(stoppingToken)) { }
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(e, "Import pass failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }
}

/// <summary>The owner's import screen (docs/specs/import.md): start one, and see how each went.</summary>
public static class ImportApi
{
    public sealed record StartBody(
        [property: JsonPropertyName("source_url")] string? SourceUrl,
        [property: JsonPropertyName("profile_base")] string? ProfileBase,
        [property: JsonPropertyName("author_map")] Dictionary<string, string>? AuthorMap,
        [property: JsonPropertyName("update_changed")] bool UpdateChanged);

    public static void MapImportApi(this WebApplication app)
    {
        var api = app.MapOwnerApi();

        api.MapGet("/blogs/{blogId:guid}/imports", async (HttpContext ctx, Guid blogId, Members members, ImportJobs jobs) =>
            await OwnerAsync(ctx, members, blogId) is null
                ? SignIn.Problem(404, "not_found", "No such blog of yours.")
                : Results.Json(new { imports = (await jobs.ListAsync(blogId, ctx.RequestAborted)).Select(Json) }));

        api.MapPost("/blogs/{blogId:guid}/imports", async (HttpContext ctx, Guid blogId, StartBody body, Members members, ImportJobs jobs) =>
        {
            if (await OwnerAsync(ctx, members, blogId) is not { } owner) return SignIn.Problem(404, "not_found", "No such blog of yours.");
            Dictionary<string, Guid>? map = null;
            if (body.AuthorMap is { Count: > 0 })
            {
                map = [];
                foreach (var (key, value) in body.AuthorMap)
                {
                    if (!Guid.TryParse(value, out var account))
                        return SignIn.Problem(400, "invalid_argument", "An author map's value is the id of an account on this blog.");
                    map[key] = account;
                }
            }
            var (job, error) = await jobs.StartAsync(owner, body.SourceUrl, body.ProfileBase, map, ctx.RequestAborted, body.UpdateChanged);
            return job is null ? SignIn.Problem(400, "invalid_argument", error!) : Results.Json(new { import = Json(job) }, statusCode: 201);
        });
    }

    private static object Json(ImportJob j) => new
    {
        id = j.Id, source = j.Source, source_url = j.SourceUrl, profile_base = j.ProfileBase, state = j.State,
        counts = JsonNode.Parse(j.Counts), log = JsonNode.Parse(j.Log), error = j.Error, update_changed = j.UpdateChanged,
        created_at = j.CreatedAt, started_at = j.StartedAt, finished_at = j.FinishedAt,
    };

    private static async Task<Actor?> OwnerAsync(HttpContext ctx, Members members, Guid blogId) =>
        await members.OnBlogAsync(blogId, SignIn.Current(ctx).AccountId, ctx.RequestAborted) is { IsOwner: true } owner ? owner : null;
}
