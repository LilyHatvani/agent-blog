using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBlog.Database;
using AgentBlog.Mail;
using AgentBlog.Media;
using AgentBlog.Posts;
using AgentBlog.Storage;
using AgentKit.Export;
using AgentKit.Secrets;

namespace AgentBlog.Export;

/// <summary>
/// Export everything, and import it into another instance (docs/specs/export.md, ROADMAP M5): leaving is never a project.
/// An export is one zip: <c>export.json</c> (the blog's rows, table by table, from one snapshot, by an allowlist checked
/// against the schema in a test), <c>subscribers.json</c> (confirmed addresses, opened: they are sealed under this
/// instance's key) and <c>media/{id}</c> (each picture's file). Sign-ins, tokens and sealed secrets never travel: people
/// sign in again on the new instance, connections arrive revoked, and channels, social accounts and Cloudflare are
/// connected again.
/// </summary>
public sealed class BlogExport(Db db, Bucket bucket, Sealer sealer, Subscribers subscribers, IConfiguration config, TimeProvider clock)
{
    public const string Format = "agent-blog-export/1";
    public const long MaxDocumentBytes = 512L * 1024 * 1024;
    private const string Scope = "blog_id = @scope";

    private const string SignIn = "a sign-in, which is per instance: people sign in again where the blog moves";
    private const string Sealed = "sealed under this instance's key, which never leaves it: connected again where the blog moves";
    private const string Queue = "work in progress on this instance, not part of the blog";

    /// <summary>What an export holds. Every table and column of the schema is decided here (BlogExportTests checks it).</summary>
    public static readonly ExportPlan Plan = new(Format,
    [
        ExportTable.Of("blog", "id slug title description settings created_at hidden_at brief", scope: "id = @scope"),
        ExportTable.Of("account", "id display_name created_at", orderBy: "created_at, id",
            scope: "id IN (SELECT account_id FROM blog_member WHERE blog_id = @scope UNION SELECT account_id FROM author WHERE blog_id = @scope)",
            withheld: ("passkey_handle", SignIn)),
        ExportTable.Of("author", "blog_id account_id slug created_at profile_url", orderBy: "created_at, account_id", scope: Scope),
        ExportTable.Of("blog_member", "blog_id account_id role created_at", orderBy: "created_at, account_id", scope: Scope),
        ExportTable.Of("term", "id blog_id kind slug name description created_by created_at", scope: Scope),
        ExportTable.Of("connection", "id blog_id label level created_by created_at last_used_at revoked_at client_id byline_name", scope: Scope,
            withheld: ("oauth_authorization_id", "the connection's sign-in grant: tokens never travel, so it arrives revoked")),
        ExportTable.Of("media", "id blog_id status content_type size_bytes sha256 filename alt source_url upload_expires_at created_by created_at ready_at trashed_at trashed_by", scope: Scope),
        ExportTable.Of("post", "id blog_id slug status current_revision_id publish_at scheduled_by published_at trashed_at trashed_by status_before_trash created_at updated_at import_source import_updated_at import_revision_id source_feed_id", scope: Scope),
        ExportTable.Of("post_revision", "id blog_id post_id number title body_md excerpt actor created_at term_ids suggested_terms", orderBy: "post_id, number", scope: Scope),
        ExportTable.Of("approval_request", "id blog_id connection_id action post_id pinned_revision_id payload state created_at decided_at decided_by decision_note", scope: Scope),
        ExportTable.Of("blog_request", "id blog_id connection_id action media_id payload pinned state created_at decided_at decided_by decision_note", scope: Scope),
        ExportTable.Of("owner_note", "id blog_id kind note post_id request_id written_by created_at retired_at", scope: Scope),
        ExportTable.Of("old_address", "blog_id path target created_at", orderBy: "path", scope: Scope),
        ExportTable.Of("import_author", "blog_id source source_key account_id created_at", orderBy: "created_at, source_key", scope: Scope),
        ExportTable.Of("event", "id blog_id target_type target_id kind actor payload at imported", scope: Scope),
    ], new Dictionary<string, string>
    {
        ["account_identity"] = SignIn, ["account_passkey"] = SignIn, ["account_session"] = SignIn, ["passkey_ceremony"] = SignIn,
        ["provider_signin_state"] = SignIn,
        ["OpenIddictApplications"] = "agents' sign-in registrations: connections are made again", ["OpenIddictAuthorizations"] = "agents' grants: tokens never travel",
        ["OpenIddictScopes"] = "agents' sign-in scopes, the instance's own", ["OpenIddictTokens"] = "agents' tokens: they never travel",
        ["blog_secret"] = Sealed, ["channel_secret"] = Sealed, ["social_secret"] = Sealed,
        ["channel"] = "notification channels: their secrets are " + Sealed, ["push_device"] = "this instance's push subscriptions: phones subscribe again",
        ["social_account"] = "announcing accounts: their keys are " + Sealed, ["announcement"] = "announcing, tied to those accounts",
        ["cdn_link"] = "Cloudflare: its token is " + Sealed,
        ["subscriber"] = "written opened in subscribers.json (addresses are sealed under this instance's key) and sealed again on import",
        ["invitation"] = "open invitations are links into this instance: invite again where the blog moves",
        ["delivery"] = Queue, ["mail_out"] = Queue, ["media_purge"] = Queue, ["link_check"] = Queue, ["search_ping"] = Queue,
        ["import_job"] = Queue, ["notify_cursor"] = Queue,
        ["instance_secret"] = "the instance's own keys", ["instance_setup"] = "the instance's own setup", ["oauth_key"] = "the instance's own signing keys",
        ["schema_migrations"] = "the instance's own schema",
    });

    private sealed record SubscriberOut(Guid Id, string Email, DateTime CreatedAt, DateTime? ConfirmedAt);
    private sealed record MediaFile(Guid Id, string ContentType);

    /// <summary>Writes the owner's export of <paramref name="owner"/>'s blog as a zip to <paramref name="output"/>.</summary>
    public async Task WriteAsync(Actor owner, Stream output, CancellationToken ct)
    {
        if (!owner.IsOwner) throw new InvalidOperationException("Only the owner exports.");
        var blogId = owner.BlogId;
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        var header = new Dictionary<string, string>
        {
            ["blog_id"] = blogId.ToString("D"),
            ["owner"] = owner.AccountId!.Value.ToString("D"),
            ["exported_at"] = clock.GetUtcNow().ToString("O"),
            ["exported_from"] = config["Blog:PublicUrl"] ?? "",
        };
        await using (var conn = await db.OpenAsync(ct))
        await using (var entry = zip.CreateEntry("export.json", CompressionLevel.Optimal).Open())
            await Plan.WriteAsync(conn, entry, blogId, header, ct);

        var confirmed = (await subscribers.ConfirmedAsync(blogId, ct))
            .Select(s => new SubscriberOut(s.Id, s.Email!, s.CreatedAt, s.ConfirmedAt)).ToList();
        await using (var entry = zip.CreateEntry("subscribers.json", CompressionLevel.Optimal).Open())
            await JsonSerializer.SerializeAsync(entry, confirmed, Json.Options, ct);

        if (!bucket.Enabled) return;
        var files = await db.ReadAsync(tx => tx.Query<MediaFile>(
            "SELECT id, content_type FROM media WHERE blog_id = @blogId AND status IN ('ready', 'trash') ORDER BY created_at, id", new { blogId }), ct);
        foreach (var f in files)
        {
            await using var o = await bucket.OpenAsync(MediaService.Key(blogId, f.Id), ct);
            if (o is null) continue; // listed, but the bucket doesn't hold it: the row says what it was
            await using var entry = zip.CreateEntry($"media/{f.Id:D}", CompressionLevel.NoCompression).Open();
            await o.Body.CopyToAsync(entry, ct);
        }
    }

    public sealed record Imported(IReadOnlyDictionary<string, int> Counts, int Pictures, int Subscribers);

    /// <summary>
    /// Imports an export into <paramref name="owner"/>'s blog, which must have nothing in it yet. The exported owner
    /// becomes this owner; everyone else arrives as an account that claims itself from an invitation ("Invite to
    /// claim"). Ids are kept, so posts' and pictures' addresses don't change. History comes over marked as imported:
    /// nothing is notified, announced, emailed or pinged again. Returns what came over, or why not.
    /// </summary>
    public async Task<(Imported? Done, string? Why)> ImportAsync(Actor owner, string zipPath, CancellationToken ct)
    {
        if (!owner.IsOwner) return (null, "Only the owner imports.");
        var blogId = owner.BlogId;
        var ownerId = owner.AccountId!.Value;

        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.GetEntry("export.json") is not { } docEntry) return (null, "That isn't an Agent Blog export: it has no export.json.");

        if (docEntry.Length > MaxDocumentBytes) return (null, "That export is too large to import here.");
        string text;
        using (var reader = new StreamReader(docEntry.Open(), Encoding.UTF8)) text = await reader.ReadToEndAsync(ct);
        ExportDocument doc;
        try
        {
            var first = await ExportDocument.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), Format, MaxDocumentBytes, ct);
            if (!Guid.TryParse(first.Header.GetValueOrDefault("blog_id"), out var fromBlog)
                || !Guid.TryParse(first.Header.GetValueOrDefault("owner"), out var fromOwner))
                return (null, "That export doesn't say whose blog it was.");
            // The exported blog and owner become this blog and this owner, wherever they're named (rows, actors, payloads).
            text = text.Replace(fromBlog.ToString("D"), blogId.ToString("D"), StringComparison.OrdinalIgnoreCase)
                       .Replace(fromOwner.ToString("D"), ownerId.ToString("D"), StringComparison.OrdinalIgnoreCase);
            doc = await ExportDocument.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), Format, MaxDocumentBytes, ct);
        }
        catch (ExportFormatException e)
        {
            return (null, e.Message);
        }

        if (await db.ReadAsync(tx => tx.One<bool>("""
                SELECT EXISTS (SELECT 1 FROM post WHERE blog_id = @blogId) OR EXISTS (SELECT 1 FROM media WHERE blog_id = @blogId)
                    OR EXISTS (SELECT 1 FROM term WHERE blog_id = @blogId)
                    OR EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @blogId AND account_id <> @ownerId)
                """, new { blogId, ownerId }), ct))
            return (null, "An import goes into a blog with nothing in it yet: no posts, pictures, tags or people besides you.");

        var subs = new List<SubscriberOut>();
        if (zip.GetEntry("subscribers.json") is { } subsEntry)
            await using (var s = subsEntry.Open())
                subs = await JsonSerializer.DeserializeAsync<List<SubscriberOut>>(s, Json.Options, ct) ?? [];
        byte[]? subscriberKey = null;
        if (subs.Count > 0 && (subscriberKey = await subscribers.KeyAsync(blogId, ct)) is null)
            return (null, $"This export holds {subs.Count} subscribers' addresses, which are kept sealed: set Blog__Secrets__MasterKey on this server first, then import.");

        var media = doc.Rows("media");
        var pictures = media.Where(m => zip.GetEntry($"media/{m["id"]!.GetValue<string>()}") is not null).ToList();
        if (pictures.Count > 0 && !bucket.Enabled)
            return (null, $"This export holds {pictures.Count} pictures: connect a bucket on this server first (Blog__Bucket__*), then import.");

        // The files first: a failed write below leaves only files nothing points to, and importing again writes them again.
        foreach (var m in pictures)
        {
            var id = Guid.Parse(m["id"]!.GetValue<string>());
            using var ms = new MemoryStream();
            await using (var s = zip.GetEntry($"media/{id:D}")!.Open()) await s.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            await bucket.WriteAsync(MediaService.Key(blogId, id), m["content_type"]?.GetValue<string>() ?? "application/octet-stream",
                bytes, SHA256.HashData(bytes), ct);
        }

        var now = clock.GetUtcNow();
        var counts = await db.WriteAsync(async tx =>
        {
            var c = new Dictionary<string, int>();
            async Task Insert(string table, JsonObject row, string? onConflict = null)
            {
                await using var cmd = new Npgsql.NpgsqlCommand(Plan.Tables.First(t => t.Name == table).InsertSql(onConflict), tx.Conn, tx.Transaction);
                cmd.Parameters.AddWithValue("row", NpgsqlTypes.NpgsqlDbType.Jsonb, row.ToJsonString());
                c[table] = c.GetValueOrDefault(table) + await cmd.ExecuteNonQueryAsync(ct);
            }

            if (doc.Rows("blog") is [var blog, ..])
                await tx.Exec("""
                    UPDATE blog SET title = b.title, description = b.description, settings = b.settings, brief = b.brief
                    FROM jsonb_populate_record(NULL::blog, @row::jsonb) b WHERE blog.id = @blogId
                    """, new { row = blog.ToJsonString(), blogId });

            foreach (var a in doc.Rows("account").Where(a => a["id"]!.GetValue<string>() != ownerId.ToString("D")))
                await Insert("account", a, "ON CONFLICT (id) DO NOTHING");
            foreach (var a in doc.Rows("author"))
                if (a["account_id"]!.GetValue<string>() == ownerId.ToString("D"))
                    await tx.Exec("UPDATE author SET slug = @slug, profile_url = @url WHERE blog_id = @blogId AND account_id = @ownerId",
                        new { slug = a["slug"]!.GetValue<string>(), url = a["profile_url"]?.GetValue<string>(), blogId, ownerId });
                else
                    await Insert("author", a, "ON CONFLICT DO NOTHING");
            foreach (var m in doc.Rows("blog_member").Where(m => m["account_id"]!.GetValue<string>() != ownerId.ToString("D")))
                await Insert("blog_member", m, "ON CONFLICT DO NOTHING");

            foreach (var t in doc.Rows("term")) await Insert("term", t);
            foreach (var conn in doc.Rows("connection"))
            {
                // Its tokens didn't come: it's history here, and an agent connects again.
                conn["revoked_at"] ??= JsonValue.Create(now.UtcDateTime);
                await Insert("connection", conn);
            }
            foreach (var m in media) await Insert("media", m);
            foreach (var table in new[] { "post", "post_revision", "approval_request", "blog_request", "owner_note", "import_author" })
                foreach (var row in doc.Rows(table)) await Insert(table, row);
            // An old address is only ever a path on this blog: an unsafe target (a backslash a browser reads as '//host',
            // a control character) is left out, never planted as a redirect off-site (docs/specs/old-addresses.md).
            foreach (var row in doc.Rows("old_address"))
                if (OldAddress.SafeTarget(row["target"]?.GetValue<string>())) await Insert("old_address", row);

            // History, in order, under new ids (event ids are the instance's), marked imported; an undo names its event anew.
            var events = Plan.Tables.First(t => t.Name == "event");
            var withoutId = events with { Columns = [.. events.Columns.Where(col => col != "id")] };
            var renumbered = new Dictionary<long, long>();
            foreach (var e in doc.Rows("event"))
            {
                var old = e["id"]!.GetValue<long>();
                e.Remove("id");
                e["imported"] = true;
                if (e["kind"]?.GetValue<string>() == "action_undone" && e["payload"] is JsonObject p
                    && p["event"]?.GetValue<long>() is { } undone && renumbered.TryGetValue(undone, out var now2))
                    p["event"] = now2;
                await using var cmd = new Npgsql.NpgsqlCommand(withoutId.InsertSql() + " RETURNING id", tx.Conn, tx.Transaction);
                cmd.Parameters.AddWithValue("row", NpgsqlTypes.NpgsqlDbType.Jsonb, e.ToJsonString());
                renumbered[old] = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }
            c["event"] = renumbered.Count;

            foreach (var s in subs)
            {
                if (Subscribers.Normalize(s.Email) is not { } address) continue;
                var sealedEmail = sealer.Seal(address, Subscribers.ForSubscriber(blogId, s.Id));
                c["subscriber"] = c.GetValueOrDefault("subscriber") + await tx.Exec("""
                    INSERT INTO subscriber (id, blog_id, email_hash, email_ciphertext, email_nonce, email_key_id, state, created_at, confirmed_at)
                    VALUES (@Id, @blogId, @hash, @Ciphertext, @Nonce, @KeyId, 'confirmed', @CreatedAt, @ConfirmedAt)
                    ON CONFLICT DO NOTHING
                    """, new { s.Id, blogId, hash = Subscribers.Hash(subscriberKey!, address), sealedEmail.Ciphertext, sealedEmail.Nonce,
                        sealedEmail.KeyId, s.CreatedAt, s.ConfirmedAt });
            }

            await tx.Event(new EventRow(blogId, "blog", blogId, "blog_imported", owner.Value, new
            {
                from = doc.Header.GetValueOrDefault("exported_from"), exported_at = doc.Header.GetValueOrDefault("exported_at"),
                posts = c.GetValueOrDefault("post"), events = renumbered.Count,
            }), now);
            return c;
        }, ct);
        return (new Imported(counts, pictures.Count, counts.GetValueOrDefault("subscriber")), null);
    }
}
