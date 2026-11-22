using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentBlog.Database;
using AgentBlog.Posts;
using AgentBlog.Secrets;
using AgentKit.Channels;
using AgentKit.Secrets;

namespace AgentBlog.Media;

/// <summary>A blog's link to its Cloudflare zone, as the owner sees it. The token never comes back.</summary>
public sealed record CdnState(bool Connected, string? ZoneId, int? EdgeSeconds, DateTime? ConnectedAt, DateTime? LastPurgeAt,
    DateTime? FailingSince, string? LastError, bool Refused, int Waiting);

/// <summary>
/// Pictures through Cloudflare (decision 0009, docs/specs/media.md § Cloudflare). The blog's own address and rules stay;
/// Cloudflare in front of it caches pictures. Every picture carries the cache tags <c>media:{id}</c> and
/// <c>blog-media:{blog}</c>. Connected (a zone id and a token that may only purge its cache), a picture also carries
/// <c>Cloudflare-CDN-Cache-Control: max-age=…</c>, the edge time (7 days by default; browsers keep their hour), and
/// trashing it purges its tag at once through the <c>media_purge</c> outbox. Not connected, the edge keeps a copy for
/// the hour browsers do: the stated bound for a trashed picture to disappear everywhere.
/// </summary>
public sealed partial class CdnLinks(Db db, Sealer sealer, IHttpClientFactory http, IConfiguration config, TimeProvider clock)
{
    public const string HttpClientName = "blog-cloudflare";
    public const int DefaultEdgeSeconds = 7 * 24 * 3600;
    public const int MinEdgeSeconds = 3600;
    public const int MaxEdgeSeconds = 30 * 24 * 3600;
    public const int MaxTagsPerCall = 100;
    private const string TokenName = "cloudflare_token";

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex ZoneShape();

    public static string Tag(Guid mediaId) => $"media:{mediaId:D}";

    public static string BlogTag(Guid blogId) => $"blog-media:{blogId:D}";

    /// <summary>The edge time for this blog's pictures while purging works, or null (connected and refused counts as not).</summary>
    public Task<int?> EdgeSecondsAsync(Guid blogId, CancellationToken ct) => db.ReadAsync(tx => tx.One<int?>("""
        SELECT edge_seconds FROM cdn_link WHERE blog_id = @blogId AND connected_at IS NOT NULL AND NOT refused
        """, new { blogId }), ct);

    public Task<CdnState> StateAsync(Guid blogId, CancellationToken ct) => db.ReadAsync(async tx =>
        await tx.One<CdnState>("""
            SELECT l.connected_at IS NOT NULL AS connected, l.zone_id, l.edge_seconds, l.connected_at, l.last_purge_at,
                   l.failing_since, l.last_error, l.refused,
                   (SELECT count(*)::int FROM media_purge p WHERE p.blog_id = l.blog_id) AS waiting
            FROM cdn_link l WHERE l.blog_id = @blogId AND l.connected_at IS NOT NULL
            """, new { blogId }) ?? new CdnState(false, null, null, null, null, null, null, false, 0), ct);

    /// <summary>
    /// Connects the blog's zone. The token is tried first with a real purge of a tag nothing carries, so a token that
    /// can't purge this zone is refused here, with Cloudflare's reason, not discovered at the first trash.
    /// </summary>
    public async Task<(CdnState? State, string? Error)> ConnectAsync(Actor owner, string? zoneId, string? token, int? edgeSeconds, CancellationToken ct)
    {
        if (!sealer.On) return (null, BlogSecrets.OffReason);
        zoneId = (zoneId ?? "").Trim().ToLowerInvariant();
        token = (token ?? "").Trim();
        if (!ZoneShape().IsMatch(zoneId)) return (null, "The zone id is 32 letters and digits: Cloudflare shows it on the domain's Overview page, under API.");
        if (token.Length is < 20 or > 200) return (null, "Paste the API token Cloudflare showed once when you made it.");
        var edge = edgeSeconds ?? DefaultEdgeSeconds;
        if (edge is < MinEdgeSeconds or > MaxEdgeSeconds) return (null, "The edge time is between an hour and 30 days.");

        var check = await PurgeAsync(zoneId, token, ["agentblog-connect-check"], ct);
        if (check.Result != SendResult.Sent) return (null, Why(check));

        var now = clock.GetUtcNow();
        var sealedToken = sealer.Seal(token, BlogSecrets.ForBlog(owner.BlogId, TokenName));
        await db.WriteAsync(async tx =>
        {
            await tx.Exec("""
                INSERT INTO blog_secret (blog_id, name, ciphertext, nonce, key_id, created_at)
                VALUES (@BlogId, @TokenName, @Ciphertext, @Nonce, @KeyId, @now)
                ON CONFLICT (blog_id, name) DO UPDATE SET ciphertext = EXCLUDED.ciphertext, nonce = EXCLUDED.nonce,
                    key_id = EXCLUDED.key_id, created_at = EXCLUDED.created_at
                """, new { owner.BlogId, TokenName, sealedToken.Ciphertext, sealedToken.Nonce, sealedToken.KeyId, now });
            await tx.Exec("""
                INSERT INTO cdn_link (blog_id, zone_id, edge_seconds, connected_by, connected_at)
                VALUES (@BlogId, @zoneId, @edge, @AccountId, @now)
                ON CONFLICT (blog_id) DO UPDATE SET zone_id = EXCLUDED.zone_id, edge_seconds = EXCLUDED.edge_seconds,
                    connected_by = EXCLUDED.connected_by, connected_at = EXCLUDED.connected_at, disconnected_at = NULL,
                    failing_since = NULL, last_error = NULL, refused = false
                """, new { owner.BlogId, zoneId, edge, owner.AccountId, now });
            await tx.Event(new EventRow(owner.BlogId, "blog", owner.BlogId, "cdn_connected", owner.Value,
                new { provider = "cloudflare", zone_id = zoneId, edge_seconds = edge }), now);
            return 0;
        }, ct);
        return (await StateAsync(owner.BlogId, ct), null);
    }

    /// <summary>
    /// Disconnects. Every picture of this blog is purged from the edge first (its blog tag), so nothing stays cached
    /// for the long edge time once trash stops purging; then the token is deleted. The owner's wish wins: a purge that
    /// fails doesn't keep the token, and the answer says what is left at the edge.
    /// </summary>
    public async Task<(bool Done, string? Note)> DisconnectAsync(Actor owner, CancellationToken ct)
    {
        var link = await db.ReadAsync(tx => tx.One<LinkRow>("""
            SELECT zone_id, edge_seconds FROM cdn_link WHERE blog_id = @BlogId AND connected_at IS NOT NULL
            """, new { owner.BlogId }), ct);
        if (link is null) return (false, null);
        string? note = null;
        if (await TokenAsync(owner.BlogId, ct) is { } token)
        {
            var purge = await PurgeAsync(link.ZoneId, token, [BlogTag(owner.BlogId)], ct);
            if (purge.Result != SendResult.Sent)
                note = $"Cloudflare didn't take the last purge ({Why(purge)}): pictures it already holds can stay at its edge for up to {Days(link.EdgeSeconds)}.";
        }
        var now = clock.GetUtcNow();
        await db.WriteAsync(async tx =>
        {
            await tx.Exec("DELETE FROM blog_secret WHERE blog_id = @BlogId AND name = @TokenName", new { owner.BlogId, TokenName });
            await tx.Exec("DELETE FROM media_purge WHERE blog_id = @BlogId", new { owner.BlogId });
            await tx.Exec("UPDATE cdn_link SET connected_at = NULL, disconnected_at = @now WHERE blog_id = @BlogId", new { owner.BlogId, now });
            await tx.Event(new EventRow(owner.BlogId, "blog", owner.BlogId, "cdn_disconnected", owner.Value,
                new { provider = "cloudflare", purged_all = note is null }), now);
            return 0;
        }, ct);
        return (true, note);
    }

    private sealed record LinkRow(string ZoneId, int EdgeSeconds);

    /// <summary>
    /// Queues a trashed picture's purge, inside the trash's own transaction, when the blog is connected. A second trash
    /// before the drain moves it forward and frees any claim.
    /// </summary>
    public static Task<int> EnqueueAsync(Tx tx, Guid blogId, Guid mediaId, DateTimeOffset now) => tx.Exec("""
        INSERT INTO media_purge (media_id, blog_id, enqueued_at)
        SELECT @mediaId, @blogId, @now WHERE EXISTS (SELECT 1 FROM cdn_link WHERE blog_id = @blogId AND connected_at IS NOT NULL)
        ON CONFLICT (media_id) DO UPDATE SET enqueued_at = EXCLUDED.enqueued_at, claimed_until = NULL, claim_id = NULL, attempts = 0
        """, new { blogId, mediaId, now });

    // ------------------------------------------------------------------------------------------------ the drain

    private sealed record Due(Guid BlogId, string ZoneId, int EdgeSeconds, bool Refused);

    /// <summary>
    /// One pass: drop the moot rows (older than their blog's edge time), then for each connected blog with pictures
    /// waiting, claim at most 100 and send them in one call (one call per blog per pass, well inside Cloudflare's purge
    /// limit). Sent: gone. Not now (429, 5xx, no answer): freed for the next pass, the link shows the trouble. Refused
    /// (401, 403, 404, another 4xx): the link is marked refused, the owner is told once (<c>cdn_purge_refused</c>), new
    /// copies get only the hour, and the rows wait until they're moot or the token works again.
    /// </summary>
    public async Task<int> DrainAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.OperationalAsync(tx => tx.Exec("""
            DELETE FROM media_purge p USING cdn_link l
            WHERE p.blog_id = l.blog_id AND p.enqueued_at < @now - make_interval(secs => l.edge_seconds)
            """, new { now }), ct);
        var blogs = await db.ReadAsync(tx => tx.Query<Due>("""
            SELECT l.blog_id, l.zone_id, l.edge_seconds, l.refused FROM cdn_link l
            WHERE l.connected_at IS NOT NULL
              AND EXISTS (SELECT 1 FROM media_purge p WHERE p.blog_id = l.blog_id AND (p.claimed_until IS NULL OR p.claimed_until < @now))
            """, new { now }), ct);
        var purged = 0;
        foreach (var blog in blogs)
        {
            var claimId = Guid.CreateVersion7();
            var ids = (await db.OperationalAsync(tx => tx.Query<Guid>("""
                UPDATE media_purge SET claimed_until = @lease, claim_id = @claimId, attempts = attempts + 1
                WHERE media_id IN (SELECT media_id FROM media_purge WHERE blog_id = @BlogId AND (claimed_until IS NULL OR claimed_until < @now)
                                   ORDER BY enqueued_at LIMIT @MaxTagsPerCall FOR UPDATE SKIP LOCKED)
                RETURNING media_id
                """, new { blog.BlogId, now, lease = now.AddMinutes(2), claimId, MaxTagsPerCall }), ct)).ToList();
            if (ids.Count == 0) continue;
            var token = await TokenAsync(blog.BlogId, ct);
            var outcome = token is null
                ? new SendOutcome(SendResult.Failed, null, null, "The token doesn't open (the master key changed?): connect again.")
                : await PurgeAsync(blog.ZoneId, token, [.. ids.Select(Tag)], ct);
            purged += await RecordAsync(blog, claimId, ids.Count, outcome, ct);
        }
        return purged;
    }

    private async Task<int> RecordAsync(Due blog, Guid claimId, int count, SendOutcome o, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (o.Result == SendResult.Sent)
        {
            return await db.OperationalAsync(async tx =>
            {
                // Only the rows this claim still holds: a picture trashed again mid-call was re-queued and goes next pass.
                var gone = await tx.Exec("DELETE FROM media_purge WHERE claim_id = @claimId", new { claimId });
                await tx.Exec("UPDATE cdn_link SET last_purge_at = @now, failing_since = NULL, last_error = NULL, refused = false WHERE blog_id = @BlogId",
                    new { blog.BlogId, now });
                return gone;
            }, ct);
        }
        var refused = o.Result is SendResult.Gone or SendResult.Failed;
        var error = Why(o);
        await db.WriteAsync(async tx =>
        {
            await tx.Exec("UPDATE media_purge SET claimed_until = NULL, claim_id = NULL WHERE claim_id = @claimId", new { claimId });
            await tx.Exec("""
                UPDATE cdn_link SET failing_since = coalesce(failing_since, @now), last_error = @error, refused = refused OR @refused
                WHERE blog_id = @BlogId
                """, new { blog.BlogId, now, error, refused });
            if (refused && !blog.Refused)
                await tx.Event(new EventRow(blog.BlogId, "blog", blog.BlogId, "cdn_purge_refused", Actors.System,
                    new { reason = $"{error} Trashed pictures can stay at Cloudflare's edge for up to {Days(blog.EdgeSeconds)}; new copies now get an hour.", waiting = count }), now);
            return 0;
        }, ct);
        return 0;
    }

    // ---------------------------------------------------------------------------------------------- Cloudflare

    /// <summary>One purge by tag: <c>POST /zones/{zone}/purge_cache</c> with the token as bearer.</summary>
    private async Task<SendOutcome> PurgeAsync(string zoneId, string token, IReadOnlyList<string> tags, CancellationToken ct)
    {
        var api = (config["Blog:Cloudflare:ApiBase"] is { Length: > 0 } b ? b : "https://api.cloudflare.com/client/v4/").TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{api}/zones/{zoneId}/purge_cache")
        {
            Content = JsonContent.Create(new { tags }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var client = http.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(15);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return await SendOutcome.ReadAsync(response, clock.GetUtcNow(), ct);
            // A 200 can still say no: Cloudflare's envelope carries success and errors. Its refusals come as 4xx, so a
            // 200 without success is a glitch: tried again next pass, never taken for a purge.
            var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            return body?["success"]?.GetValue<bool>() == true
                ? new SendOutcome(SendResult.Sent, 200, null, null)
                : new SendOutcome(SendResult.Retry, 200, null, Text.Truncate(body?["errors"]?.ToJsonString() ?? "Cloudflare answered without success.", 400));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            return SendOutcome.NoAnswer(e is TaskCanceledException ? "Cloudflare didn't answer within 15 seconds." : e.Message);
        }
    }

    private async Task<string?> TokenAsync(Guid blogId, CancellationToken ct)
    {
        var row = await db.ReadAsync(tx => tx.One<SecretRow>(
            "SELECT ciphertext, nonce, key_id FROM blog_secret WHERE blog_id = @blogId AND name = @TokenName", new { blogId, TokenName }), ct);
        return row is null ? null : sealer.OpenText(new SealedValue(row.Ciphertext, row.Nonce, row.KeyId), BlogSecrets.ForBlog(blogId, TokenName));
    }

    private sealed record SecretRow(byte[] Ciphertext, byte[] Nonce, string KeyId);

    private static string Why(SendOutcome o) => o switch
    {
        { Status: 401 or 403 } => $"Cloudflare refused the token ({o.Status}): it needs Zone → Cache Purge on this zone.",
        { Status: 404 } => "Cloudflare doesn't know that zone (404): check the zone id.",
        { Status: 429 } => "Cloudflare's purge limit was reached (429); the next pass tries again.",
        { Result: SendResult.Retry } => $"Cloudflare didn't take it this time ({o.Error}); the next pass tries again.",
        _ => o.Error ?? "Cloudflare said no.",
    };

    private static string Days(int seconds) => seconds >= 86400 ? $"{seconds / 86400} day{(seconds >= 2 * 86400 ? "s" : "")}" : $"{seconds / 3600} hour{(seconds >= 7200 ? "s" : "")}";
}
