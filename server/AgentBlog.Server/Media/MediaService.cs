using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentBlog.Database;
using AgentBlog.Posts;
using AgentBlog.Storage;

namespace AgentBlog.Media;

public sealed record MediaView(Guid Id, Guid BlogId, string Status, string ContentType, long SizeBytes, byte[] Sha256, string Filename,
    string Alt, string? SourceUrl, string CreatedBy, string CreatedByLabel, DateTime CreatedAt, DateTime? ReadyAt,
    DateTime? UploadExpiresAt, DateTime? TrashedAt)
{
    /// <summary>The reader's address, relative to the blog: what goes in a post's Markdown.</summary>
    public string Path => $"/media/{Id:D}/{Filename}";

    public string Markdown => $"![{EscapeAlt(Alt)}]({Path})";

    private static string EscapeAlt(string alt) => alt.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]").ReplaceLineEndings(" ");
}

/// <summary>What a media call came to: the media (and an upload slot to PUT to), or a refusal with its reason.</summary>
public sealed record MediaOutcome(MediaView? Media = null, PresignedRequest? Upload = null, string? Code = null, string? Message = null)
{
    public bool Ok => Code is null;

    public static MediaOutcome Fail(string code, string message) => new(Code: code, Message: message);
}

/// <summary>
/// The media rules (docs/specs/media.md), one place for the MCP tools and the owner's API. A picture reaches the bucket
/// two ways: the agent PUTs it to a presigned slot and confirms, or the server fetches it from a URL through
/// <see cref="SafeFetch"/>. Either way the server reads the bytes back (or has them in hand) and checks them before the
/// picture is ready: the declared length and SHA-256, and that the bytes are the picture type they claim. Uploading runs
/// at Propose (decision 0001); trash and restore are the owner's for now (agent media trash waits for the approval
/// request to cover media, docs/specs/tools.md).
/// </summary>
public sealed partial class MediaService(Db db, Bucket bucket, IHttpClientFactory http, TimeProvider clock)
{
    public const int MaxAlt = 500;
    public const int MaxUrl = 2000;

    public bool Enabled => bucket.Enabled;

    public static string Key(Guid blogId, Guid id) => $"blogs/{blogId:D}/media/{id:D}";

    private const string Select = """
        SELECT id, blog_id, status, content_type, size_bytes, sha256, filename, alt, source_url, created_by,
               actor_label(created_by) AS created_by_label, created_at, ready_at, upload_expires_at, trashed_at
        FROM media
        """;

    private static MediaOutcome Off() => MediaOutcome.Fail("media_off",
        "This blog has no bucket configured, so it can't keep pictures yet. The owner sets Blog__Bucket__* on the server.");

    // ----------------------------------------------------------------------------------------------- reads

    public Task<IReadOnlyList<MediaView>> ListAsync(Guid blogId, string? status, int limit, int offset, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<MediaView>>(async tx => [.. await tx.Query<MediaView>($"""
            {Select}
            WHERE blog_id = @blogId AND (@status::text IS NULL OR status = @status)
                  AND (@status::text IS NOT NULL OR status IN ('ready', 'trash'))
            ORDER BY created_at DESC, id
            LIMIT @limit OFFSET @offset
            """, new { blogId, status, limit = Math.Clamp(limit, 1, 100), offset = Math.Max(offset, 0) })], ct);

    public Task<MediaView?> GetAsync(Guid blogId, Guid id, CancellationToken ct) =>
        db.ReadAsync(tx => tx.One<MediaView>($"{Select} WHERE blog_id = @blogId AND id = @id", new { blogId, id }), ct);

    /// <summary>The blog a media item belongs to, when the account owns it; null otherwise (reads as not found).</summary>
    public Task<Guid?> OwnedBlogOfAsync(Guid accountId, Guid mediaId, CancellationToken ct) => db.ReadAsync(tx => tx.One<Guid?>("""
        SELECT m.blog_id FROM media m
        JOIN blog_member b ON b.blog_id = m.blog_id AND b.account_id = @accountId AND b.role = 'owner'
        WHERE m.id = @mediaId
        """, new { accountId, mediaId }), ct);

    /// <summary>A picture readers may see: ready (not pending, expired or in the trash), on this blog.</summary>
    public async Task<MediaView?> ReadyAsync(Guid blogId, Guid id, CancellationToken ct) =>
        bucket.Enabled && await GetAsync(blogId, id, ct) is { Status: "ready" } m ? m : null;

    /// <summary>The picture's bytes from the bucket, or null when the bucket no longer holds them.</summary>
    public Task<BucketObject?> OpenAsync(MediaView m, CancellationToken ct) => bucket.OpenAsync(Key(m.BlogId, m.Id), ct);

    // --------------------------------------------------------------------------------------- upload by PUT

    /// <summary>
    /// A slot to PUT one picture to: the server signs its type, length and SHA-256, so the bucket takes exactly those bytes.
    /// The agent PUTs, then calls <see cref="ConfirmAsync"/>; a slot never confirmed expires (<see cref="Bucket.UploadLifetime"/>).
    /// </summary>
    public async Task<MediaOutcome> RequestUploadAsync(Actor actor, string? filename, string contentType, long size, string sha256,
        string? alt, CancellationToken ct)
    {
        if (!bucket.Enabled) return Off();
        contentType = contentType.Trim().ToLowerInvariant();
        if (!PictureType.Allowed.Contains(contentType))
            return MediaOutcome.Fail("unsupported_type", $"Pictures only: {string.Join(", ", PictureType.Allowed)}. SVG isn't kept: it can carry script.");
        if (size < 1 || size > bucket.MaxBytes)
            return MediaOutcome.Fail("too_large", $"A picture is 1 to {bucket.MaxBytes} bytes; this one is {size}.");
        if (ParseSha256(sha256) is not { } hash)
            return MediaOutcome.Fail("invalid_argument", "sha256 is the file's SHA-256: 64 hex characters, or 44 characters of base64.");
        if ((alt ?? "").Length > MaxAlt) return MediaOutcome.Fail("invalid_argument", $"alt is at most {MaxAlt} characters.");

        var now = clock.GetUtcNow();
        var expires = now + Bucket.UploadLifetime;
        var id = Guid.CreateVersion7();
        var name = FileName(filename, contentType);
        var refused = await db.WriteAsync(async tx =>
        {
            if (await OverQuotaAsync(tx, actor.BlogId, size) is { } full) return full;
            await tx.Exec("""
                INSERT INTO media (id, blog_id, status, content_type, size_bytes, sha256, filename, alt, upload_expires_at, created_by, created_at)
                VALUES (@id, @BlogId, 'pending', @contentType, @size, @hash, @name, @alt, @expires, @Value, @now)
                """, new { id, actor.BlogId, contentType, size, hash, name, alt = alt ?? "", expires, actor.Value, now });
            await tx.Event(new EventRow(actor.BlogId, "media", id, "media_upload_started", actor.Value,
                new { filename = name, content_type = contentType, size_bytes = size, via = "upload" }), now);
            return null;
        }, ct);
        if (refused is not null) return refused;
        var upload = bucket.Upload(Key(actor.BlogId, id), contentType, size, hash, expires);
        return new MediaOutcome(await GetAsync(actor.BlogId, id, ct), upload);
    }

    /// <summary>
    /// Reads the PUT's bytes back from the bucket and checks them: the length and SHA-256 the slot was signed for, and a
    /// picture of the type declared. Bytes that fail are removed, and the slot stays open until it expires, for another PUT.
    /// Only the actor who asked for the slot (or the owner) confirms it.
    /// </summary>
    public async Task<MediaOutcome> ConfirmAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (!bucket.Enabled) return Off();
        if (await GetAsync(actor.BlogId, id, ct) is not { } m || (!actor.IsOwner && m.CreatedBy != actor.Value))
            return MediaOutcome.Fail("not_found", "No such upload from this connection.");
        if (m.Status == "ready") return new MediaOutcome(m); // confirming twice is harmless
        if (m.Status != "pending" || m.UploadExpiresAt < clock.GetUtcNow().UtcDateTime)
            return MediaOutcome.Fail("expired", "This upload slot has expired. Ask for a new one with upload_media.");

        var key = Key(actor.BlogId, id);
        var bytes = await bucket.ReadAsync(key, m.SizeBytes, ct);
        if (bytes is null)
            return MediaOutcome.Fail("not_uploaded", "Nothing has arrived at the upload address yet. PUT the file there with exactly the headers given, then confirm.");
        string? wrong = null;
        if (bytes.LongLength != m.SizeBytes) wrong = $"The bucket holds {(bytes.LongLength > m.SizeBytes ? "more than" : bytes.LongLength.ToString())} bytes, not the {m.SizeBytes} declared.";
        else if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(m.Sha256)) wrong = "The bytes in the bucket don't have the SHA-256 declared.";
        else if (PictureType.Sniff(bytes) is var sniffed && sniffed != m.ContentType)
            wrong = sniffed is null ? $"The file isn't a picture; it was declared {m.ContentType}." : $"The file is {sniffed}, not the {m.ContentType} declared.";
        if (wrong is not null)
        {
            await bucket.DeleteAsync(key, ct);
            return MediaOutcome.Fail("upload_mismatch", wrong + " It was removed; PUT the right file to the same address before it expires, or ask for a new slot.");
        }
        return await MarkReadyAsync(actor, m, ct);
    }

    // ------------------------------------------------------------------------------------------- from a URL

    /// <summary>
    /// The server fetches a public picture itself (an agent that can't send bytes, or a picture already on the web). The
    /// fetch is guarded (<see cref="SafeFetch"/>), bounded in time and size, and what arrives must be one of the four
    /// picture types, whatever the response says it is.
    /// </summary>
    public async Task<MediaOutcome> FromUrlAsync(Actor actor, string url, string? alt, string? filename, CancellationToken ct)
    {
        if (!bucket.Enabled) return Off();
        if (url.Length > MaxUrl || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0)
            return MediaOutcome.Fail("invalid_argument", "url is an absolute http or https address, without a user name or password.");
        if ((alt ?? "").Length > MaxAlt) return MediaOutcome.Fail("invalid_argument", $"alt is at most {MaxAlt} characters.");

        byte[] bytes;
        try
        {
            using var client = http.CreateClient(SafeFetch.HttpClientName);
            client.Timeout = SafeFetch.Timeout;
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Accept.ParseAdd("image/png, image/jpeg, image/gif, image/webp");
            req.Headers.UserAgent.ParseAdd("AgentBlog/1 (media fetch)");
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
                return MediaOutcome.Fail("fetch_failed", $"{uri.Host} answered {(int)res.StatusCode}.");
            if (res.Content.Headers.ContentLength > bucket.MaxBytes)
                return MediaOutcome.Fail("too_large", $"The picture is {res.Content.Headers.ContentLength} bytes; the limit is {bucket.MaxBytes}.");
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > bucket.MaxBytes)
                    return MediaOutcome.Fail("too_large", $"The picture is over the limit of {bucket.MaxBytes} bytes.");
            }
            bytes = buffer.ToArray();
        }
        catch (Exception e) when (SafeFetch.Blocked(e) is { } blocked)
        {
            return MediaOutcome.Fail("address_not_allowed", blocked.Message + " The server fetches only from the public internet.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return MediaOutcome.Fail("fetch_failed", e is TaskCanceledException
                ? $"{uri.Host} didn't answer within {SafeFetch.Timeout.TotalSeconds:0} seconds."
                : $"Couldn't fetch from {uri.Host}: {e.Message}");
        }

        if (bytes.Length == 0) return MediaOutcome.Fail("not_a_picture", "The address answered with nothing.");
        if (PictureType.Sniff(bytes) is not { } type)
            return MediaOutcome.Fail("not_a_picture", $"What {uri.Host} sent isn't a PNG, JPEG, GIF or WebP picture.");

        var hash = SHA256.HashData(bytes);
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        var name = FileName(filename ?? Uri.UnescapeDataString(uri.AbsolutePath.Split('/')[^1]), type);
        var sourceUrl = uri.AbsoluteUri;
        // Pending first, so bytes that reach the bucket always have a row the sweep can find if anything below fails.
        var refused = await db.WriteAsync(async tx =>
        {
            if (await OverQuotaAsync(tx, actor.BlogId, bytes.Length) is { } full) return full;
            await tx.Exec("""
                INSERT INTO media (id, blog_id, status, content_type, size_bytes, sha256, filename, alt, source_url, upload_expires_at, created_by, created_at)
                VALUES (@id, @BlogId, 'pending', @type, @size, @hash, @name, @alt, @sourceUrl, @expires, @Value, @now)
                """, new { id, actor.BlogId, type, size = (long)bytes.Length, hash, name, alt = alt ?? "", sourceUrl,
                expires = now + Bucket.UploadLifetime, actor.Value, now });
            await tx.Event(new EventRow(actor.BlogId, "media", id, "media_upload_started", actor.Value,
                new { filename = name, content_type = type, size_bytes = bytes.Length, via = "url", source_url = sourceUrl }), now);
            return null;
        }, ct);
        if (refused is not null) return refused;
        await bucket.WriteAsync(Key(actor.BlogId, id), type, bytes, hash, ct);
        return await MarkReadyAsync(actor, (await GetAsync(actor.BlogId, id, ct))!, ct);
    }

    // ------------------------------------------------------------------------------------------ the owner's

    /// <summary>To the trash: readers get 404 at once, the bytes stay, and restore brings it back. The owner's only, for now.</summary>
    public Task<MediaOutcome> TrashAsync(Actor actor, Guid id, CancellationToken ct) => MoveAsync(actor, id, "ready", "trash", ct);

    public Task<MediaOutcome> RestoreAsync(Actor actor, Guid id, CancellationToken ct) => MoveAsync(actor, id, "trash", "ready", ct);

    private async Task<MediaOutcome> MoveAsync(Actor actor, Guid id, string from, string to, CancellationToken ct)
    {
        if (!actor.IsOwner) return MediaOutcome.Fail("owner_only", "Only the owner trashes and restores media, for now.");
        var outcome = await db.WriteAsync(async tx =>
        {
            var now = clock.GetUtcNow();
            var status = await tx.One<string>("SELECT status FROM media WHERE blog_id = @BlogId AND id = @id FOR UPDATE", new { actor.BlogId, id });
            if (status is null) return MediaOutcome.Fail("not_found", "No such media on this blog.");
            if (status != from)
                return MediaOutcome.Fail("invalid_state", to == "trash" ? $"Only ready media goes to the trash; this is {status}." : "It isn't in the trash.");
            await tx.Exec(to == "trash"
                ? "UPDATE media SET status = 'trash', trashed_at = @now, trashed_by = @Value WHERE id = @id"
                : "UPDATE media SET status = 'ready', trashed_at = NULL, trashed_by = NULL WHERE id = @id",
                new { id, now, actor.Value });
            await tx.Event(new EventRow(actor.BlogId, "media", id, to == "trash" ? "media_trashed" : "media_restored", actor.Value, null), now);
            return null;
        }, ct);
        return outcome ?? new MediaOutcome(await GetAsync(actor.BlogId, id, ct));
    }

    // ---------------------------------------------------------------------------------------------- the sweep

    /// <summary>
    /// Expires upload slots that ran out unconfirmed and removes whatever bytes reached the bucket for them. A slot whose
    /// bytes can't be removed now stays pending and is tried again on the next pass.
    /// </summary>
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!bucket.Enabled) return 0;
        var due = await db.ReadAsync(tx => tx.Query<(Guid Id, Guid BlogId)>("""
            SELECT id, blog_id FROM media WHERE status = 'pending' AND upload_expires_at < @now ORDER BY upload_expires_at LIMIT 50
            """, new { now }), ct);
        var expired = 0;
        foreach (var (id, blogId) in due)
        {
            if (!await bucket.DeleteAsync(Key(blogId, id), ct)) continue;
            expired += await db.WriteAsync(async tx =>
            {
                var n = await tx.Exec("""
                    UPDATE media SET status = 'expired', upload_expires_at = NULL WHERE id = @id AND status = 'pending' AND upload_expires_at < @now
                    """, new { id, now });
                if (n == 1) await tx.Event(new EventRow(blogId, "media", id, "media_upload_expired", Actors.System, null), now);
                return n;
            }, ct);
        }
        return expired;
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private async Task<MediaOutcome> MarkReadyAsync(Actor actor, MediaView m, CancellationToken ct)
    {
        var done = await db.WriteAsync(async tx =>
        {
            var now = clock.GetUtcNow();
            var n = await tx.Exec("""
                UPDATE media SET status = 'ready', ready_at = @now, upload_expires_at = NULL WHERE id = @Id AND status = 'pending'
                """, new { m.Id, now });
            if (n == 1)
                await tx.Event(new EventRow(m.BlogId, "media", m.Id, "media_added", actor.Value,
                    new { filename = m.Filename, content_type = m.ContentType, size_bytes = m.SizeBytes }), now);
            return n == 1;
        }, ct);
        var current = await GetAsync(m.BlogId, m.Id, ct);
        return done || current?.Status == "ready"
            ? new MediaOutcome(current)
            : MediaOutcome.Fail("expired", "This upload slot expired before it was confirmed. Ask for a new one.");
    }

    /// <summary>What the blog's pictures take, pending and trash included (their bytes are in the bucket too).</summary>
    private async Task<MediaOutcome?> OverQuotaAsync(Tx tx, Guid blogId, long adding)
    {
        // One blog's uploads queue behind each other here, so two at once can't both fit under the line.
        await tx.Exec("SELECT 1 FROM blog WHERE id = @blogId FOR UPDATE", new { blogId });
        var used = await tx.One<long>("""
            SELECT coalesce(sum(size_bytes), 0)::bigint FROM media WHERE blog_id = @blogId AND status IN ('pending', 'ready', 'trash')
            """, new { blogId });
        return used + adding <= bucket.MaxBlogBytes ? null : MediaOutcome.Fail("blog_full",
            $"The blog's pictures take {used} of its {bucket.MaxBlogBytes} bytes, and this one is {adding}. The owner can raise Blog__Bucket__max_blog_bytes.");
    }

    internal static byte[]? ParseSha256(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 64 && HexOnly().IsMatch(v)) return Convert.FromHexString(v);
        if (v.Length == 44)
        {
            var bytes = new byte[33];
            if (Convert.TryFromBase64String(v, bytes, out var n) && n == 32) return bytes[..32];
        }
        return null;
    }

    /// <summary>A safe last segment for the reader's address: lower-case words joined by '-', and the type's own extension.</summary>
    internal static string FileName(string? given, string contentType)
    {
        var stem = given ?? "";
        var dot = stem.LastIndexOf('.');
        if (dot > 0) stem = stem[..dot];
        var normalized = stem.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            var l = char.ToLowerInvariant(c);
            sb.Append(l is >= 'a' and <= 'z' or >= '0' and <= '9' ? l : '-');
        }
        var slug = Dashes().Replace(sb.ToString(), "-").Trim('-');
        if (slug.Length > 80) slug = slug[..80].TrimEnd('-');
        if (slug.Length == 0) slug = "picture";
        return $"{slug}.{PictureType.Extension(contentType)}";
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex HexOnly();

    [GeneratedRegex("-{2,}")]
    private static partial Regex Dashes();
}
