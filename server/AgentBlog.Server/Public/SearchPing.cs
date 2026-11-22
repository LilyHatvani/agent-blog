using System.Net.Http.Json;
using System.Security.Cryptography;
using AgentBlog.Database;

namespace AgentBlog.Public;

/// <summary>
/// Telling search engines and feed readers that a post changed (docs/specs/social.md § Found). A worker reads the event
/// log with its own cursor; for each blog that has it on (on by default) and a public address, it sends the changed
/// posts' addresses to IndexNow (one ping reaches Bing, Yandex, Seznam, Naver and Yep; Google doesn't take part) and
/// tells the feed's WebSub hub that the feeds changed. The IndexNow key is made per blog and published at
/// <c>/{key}.txt</c>, which is how the engines know the ping came from the site. A failed ping isn't retried: the next
/// change pings again, and the sitemap is there regardless. Each service's last answer is kept for the owner.
/// </summary>
public sealed class SearchPing(Db db, IHttpClientFactory http, IConfiguration config, TimeProvider clock, ILogger<SearchPing> log)
{
    public const string HttpClientName = "blog-ping";

    /// <summary>Events that change what a public address shows.</summary>
    private static readonly string[] Kinds =
        ["post_published", "post_unpublished", "post_trashed", "post_restored", "revision_saved", "revision_restored", "blog_settings_changed"];

    private sealed record Change(long Id, Guid BlogId, string Kind, string TargetType, Guid TargetId);
    private sealed record BlogRow(Guid Id, string Slug, bool On, string? Key, bool Hidden);

    public string IndexNowEndpoint => config["Blog:IndexNow:Endpoint"] ?? "https://api.indexnow.org/indexnow";

    /// <summary>The feeds' hub, or null when WebSub is off for this server.</summary>
    public string? Hub => config["Blog:WebSub:Hub"] is { } h ? (h.Length > 0 ? h : null) : "https://pubsubhubbub.appspot.com/";

    /// <summary>A blog's public address without a request: the hosted subdomain, or this server's public address.</summary>
    public string? UrlOf(string slug) =>
        config["Blog:Hosting:Domain"]?.Trim('.') is { Length: > 0 } domain ? $"https://{slug}.{domain}/"
        : config["Blog:PublicUrl"] is { Length: > 0 } p ? p.TrimEnd('/') + "/" : null;

    /// <summary>The blog's IndexNow key, made the first time it is needed (32 hex digits: not a secret, it is published).</summary>
    public Task<string> KeyAsync(Guid blogId, CancellationToken ct) => db.OperationalAsync(async tx =>
        await tx.One<string>("""
            UPDATE blog SET settings = settings || jsonb_build_object('indexnow_key', coalesce(settings->>'indexnow_key', @fresh))
            WHERE id = @blogId RETURNING settings->>'indexnow_key'
            """, new { blogId, fresh = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) }) ?? "", ct);

    /// <summary>One pass: read what changed since the cursor, ping per blog, move the cursor. Returns the addresses sent.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var (changes, last) = await db.OperationalAsync(async tx =>
        {
            var from = await tx.One<long>("SELECT last_event_id FROM notify_cursor WHERE name = 'search' FOR UPDATE");
            var list = (await tx.Query<Change>("""
                SELECT id, blog_id, kind, target_type, target_id FROM event WHERE id > @from ORDER BY id LIMIT 1000
                """, new { from })).ToList();
            if (list.Count > 0) await tx.Exec("UPDATE notify_cursor SET last_event_id = @last WHERE name = 'search'", new { last = list[^1].Id });
            return (list.Where(c => Kinds.Contains(c.Kind)).ToList(), list.Count);
        }, ct);
        if (changes.Count == 0) return 0;

        var sent = 0;
        foreach (var group in changes.GroupBy(c => c.BlogId))
        {
            var blog = await db.ReadAsync(tx => tx.One<BlogRow>("""
                SELECT id, slug, coalesce((settings->>'search_ping')::boolean, true) AS on, settings->>'indexnow_key' AS key,
                       hidden_at IS NOT NULL AS hidden
                FROM blog WHERE id = @BlogId
                """, new { group.First().BlogId }), ct);
            if (blog is null || !blog.On || blog.Hidden || UrlOf(blog.Slug) is not { } url) continue;

            // Only addresses that are or were public: a post live now, or one published once that left (a 404 the
            // engines should learn about). A draft that never went live is never named: its address would tell
            // the search engines what isn't published yet. An edit matters only while the post is live.
            var postIds = group.Where(c => c.TargetType == "post").Select(c => c.TargetId).Distinct().ToArray();
            var posts = (await db.ReadAsync(tx => tx.Query<(Guid Id, string Slug, string Status, bool WasPublic)>(
                "SELECT id, slug, status, published_at IS NOT NULL AS was_public FROM post WHERE blog_id = @Id AND id = ANY (@postIds)",
                new { blog.Id, postIds }), ct)).ToDictionary(p => p.Id);
            var urls = new List<string>();
            var feeds = false;
            foreach (var c in group)
            {
                if (c.Kind == "blog_settings_changed") { urls.Add(url); feeds = true; continue; }
                if (!posts.TryGetValue(c.TargetId, out var p)) continue;
                var edit = c.Kind is "revision_saved" or "revision_restored";
                if (p.Status != "live" && (edit || !p.WasPublic)) continue;
                urls.Add($"{url}posts/{Uri.EscapeDataString(p.Slug)}");
                feeds = true;
            }
            urls = [.. urls.Distinct()];
            if (urls.Count == 0) continue;

            var key = blog.Key ?? await KeyAsync(blog.Id, ct);
            await PingIndexNowAsync(blog.Id, url, key, urls, ct);
            if (feeds && Hub is { } hub) await PingHubAsync(blog.Id, hub, [$"{url}feed.xml", $"{url}atom.xml"], ct);
            sent += urls.Count;
        }
        return sent;
    }

    private async Task PingIndexNowAsync(Guid blogId, string url, string key, List<string> urls, CancellationToken ct)
    {
        int? status = null;
        string? error = null;
        try
        {
            using var client = http.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(15);
            using var res = await client.PostAsJsonAsync(IndexNowEndpoint, new
            {
                host = new Uri(url).Host,
                key,
                keyLocation = $"{url}{key}.txt",
                urlList = urls,
            }, ct);
            status = (int)res.StatusCode;
            // 200 and 202 are taken; 422 means the addresses aren't on this host or the key didn't check out.
            if (status is not (200 or 202)) error = $"IndexNow answered {status}.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            error = $"IndexNow didn't answer ({e.Message}).";
        }
        await RecordAsync(blogId, "indexnow", status, error, urls.Count, ct);
    }

    private async Task PingHubAsync(Guid blogId, string hub, string[] feeds, CancellationToken ct)
    {
        int? status = null;
        string? error = null;
        try
        {
            using var client = http.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(15);
            foreach (var feed in feeds)
            {
                using var res = await client.PostAsync(hub, new FormUrlEncodedContent([new("hub.mode", "publish"), new("hub.url", feed)]), ct);
                status = (int)res.StatusCode;
                if (status is < 200 or >= 300) { error = $"The WebSub hub answered {status}."; break; }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            error = $"The WebSub hub didn't answer ({e.Message}).";
        }
        await RecordAsync(blogId, "websub", status, error, feeds.Length, ct);
    }

    private Task RecordAsync(Guid blogId, string service, int? status, string? error, int count, CancellationToken ct)
    {
        if (error is not null) log.LogInformation("Search ping {Service} for blog {Blog}: {Error}", service, blogId, error);
        return db.OperationalAsync(tx => tx.Exec("""
            INSERT INTO search_ping (blog_id, service, last_at, last_status, last_error, last_urls) VALUES (@blogId, @service, @now, @status, @error, @count)
            ON CONFLICT (blog_id, service) DO UPDATE SET last_at = EXCLUDED.last_at, last_status = EXCLUDED.last_status,
                last_error = EXCLUDED.last_error, last_urls = EXCLUDED.last_urls
            """, new { blogId, service, now = clock.GetUtcNow(), status, error, count }), ct);
    }

    public sealed record PingState(string Service, DateTime LastAt, int? LastStatus, string? LastError, int LastUrls);

    public Task<IReadOnlyList<PingState>> StateAsync(Guid blogId, CancellationToken ct) => db.ReadAsync(async tx =>
        (IReadOnlyList<PingState>)[.. await tx.Query<PingState>(
            "SELECT service, last_at, last_status, last_error, last_urls FROM search_ping WHERE blog_id = @blogId ORDER BY service", new { blogId })], ct);

    /// <summary>The background loop: every <c>Blog__SearchPing__IntervalSeconds</c> (60; 0 turns it off, as the tests do).</summary>
    public sealed class Worker(SearchPing ping, IConfiguration config, TimeProvider clock, ILogger<Worker> log) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var seconds = config.GetValue("Blog:SearchPing:IntervalSeconds", 60);
            if (seconds <= 0) return;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await ping.RunOnceAsync(stoppingToken); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning(e, "The search ping pass failed; it runs again in {Seconds} s.", seconds);
                }
            }
        }
    }
}
