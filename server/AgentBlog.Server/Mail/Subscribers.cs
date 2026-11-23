using System.Collections.Concurrent;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using AgentBlog.Database;
using AgentBlog.Posts;
using AgentBlog.Secrets;
using AgentKit.Secrets;

namespace AgentBlog.Mail;

/// <summary>
/// Readers who asked for new posts by email (docs/specs/subscribe.md). Each address is sealed under the master key and
/// found again by an HMAC under a per-blog key; nothing else about a reader is kept. Confirmation and unsubscribe links
/// carry the subscriber's id and a token derived from it with the same key, so no token is stored anywhere.
/// </summary>
public sealed class Subscribers(Db db, Sealer sealer, TimeProvider clock)
{
    public const string KeyName = "subscriber_key";
    public static readonly TimeSpan ConfirmFor = TimeSpan.FromHours(48);
    public static readonly TimeSpan ConfirmEvery = TimeSpan.FromHours(1);

    public enum Outcome { Sent, Quiet, Invalid, Off }

    /// <summary>The blog's subscriber key, made the first time it's needed. Null without the master key.</summary>
    public async Task<byte[]?> KeyAsync(Guid blogId, CancellationToken ct)
    {
        if (!sealer.On) return null;
        var row = await db.ReadAsync(tx => tx.One<SecretRow>(
            "SELECT ciphertext, nonce, key_id FROM blog_secret WHERE blog_id = @blogId AND name = @KeyName", new { blogId, KeyName }), ct);
        if (row is null)
        {
            var fresh = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var sealedKey = sealer.Seal(fresh, BlogSecrets.ForBlog(blogId, KeyName));
            await db.OperationalAsync(tx => tx.Exec("""
                INSERT INTO blog_secret (blog_id, name, ciphertext, nonce, key_id, created_at) VALUES (@blogId, @KeyName, @Ciphertext, @Nonce, @KeyId, @now)
                ON CONFLICT (blog_id, name) DO NOTHING
                """, new { blogId, KeyName, sealedKey.Ciphertext, sealedKey.Nonce, sealedKey.KeyId, now = clock.GetUtcNow() }), ct);
            row = await db.ReadAsync(tx => tx.One<SecretRow>(
                "SELECT ciphertext, nonce, key_id FROM blog_secret WHERE blog_id = @blogId AND name = @KeyName", new { blogId, KeyName }), ct);
        }
        return row is not null && sealer.OpenText(new SealedValue(row.Ciphertext, row.Nonce, row.KeyId), BlogSecrets.ForBlog(blogId, KeyName)) is { } hex
            ? Convert.FromHexString(hex) : null;
    }

    private sealed record SecretRow(byte[] Ciphertext, byte[] Nonce, string KeyId);

    /// <summary>An address as the blog keeps it: trimmed and lower-cased; null when it isn't one.</summary>
    public static string? Normalize(string? email)
    {
        var e = (email ?? "").Trim().ToLowerInvariant();
        if (e.Length is < 3 or > 254 || e.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || e.Count(c => c == '@') != 1) return null;
        try
        {
            var parsed = new MailAddress(e);
            var domain = parsed.Host;
            return parsed.Address == e && domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.') ? e : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static byte[] Hash(byte[] key, string normalized) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("email:" + normalized));

    /// <summary>The token in a subscriber's links: an HMAC of what the link is for, never stored.</summary>
    public static string Token(byte[] key, string purpose, Guid subscriberId, DateTime? expires = null) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(
            $"{purpose}:{subscriberId:D}:{(expires is { } x ? DateTime.SpecifyKind(x, DateTimeKind.Utc).Ticks : 0)}")))[..40];

    private static bool Same(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    public static byte[] ForSubscriber(Guid blogId, Guid id) => Sealer.AssociatedData("agentblog-subscriber:v1", blogId.ToString(), id.ToString());

    /// <summary>Whether the blog takes subscribers now: an Email account that is on, and the master key.</summary>
    public Task<bool> OpenAsync(Guid blogId, CancellationToken ct) => sealer.On
        ? db.ReadAsync(tx => tx.One<bool>(
            "SELECT EXISTS (SELECT 1 FROM social_account WHERE blog_id = @blogId AND network = 'email' AND enabled AND removed_at IS NULL)",
            new { blogId }), ct)
        : Task.FromResult(false);

    private sealed record Existing(Guid Id, string State, DateTime? ConfirmSentAt);

    /// <summary>
    /// A reader asks to subscribe. The answer reads the same for a new, waiting or confirmed address (<see cref="Outcome.Sent"/>
    /// or <see cref="Outcome.Quiet"/>, shown alike): only a new or waiting address gets a confirmation, at most once an hour.
    /// </summary>
    public async Task<Outcome> SubscribeAsync(Guid blogId, string? email, CancellationToken ct)
    {
        if (!await OpenAsync(blogId, ct)) return Outcome.Off;
        if (Normalize(email) is not { } address) return Outcome.Invalid;
        if (await KeyAsync(blogId, ct) is not { } key) return Outcome.Off;
        var hash = Hash(key, address);
        var now = clock.GetUtcNow();
        return await db.WriteAsync(async tx =>
        {
            var existing = await tx.One<Existing>(
                "SELECT id, state, confirm_sent_at FROM subscriber WHERE blog_id = @blogId AND email_hash = @hash FOR UPDATE", new { blogId, hash });
            if (existing is { State: "confirmed" }) return Outcome.Quiet;
            if (existing is { ConfirmSentAt: { } last } && now - new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) < ConfirmEvery)
                return Outcome.Quiet;
            var id = existing?.Id ?? Guid.CreateVersion7();
            var sealedEmail = sealer.Seal(address, ForSubscriber(blogId, id));
            var expires = now + ConfirmFor;
            if (existing is null)
                await tx.Exec("""
                    INSERT INTO subscriber (id, blog_id, email_hash, email_ciphertext, email_nonce, email_key_id, state, confirm_expires_at, confirm_sent_at, created_at)
                    VALUES (@id, @blogId, @hash, @Ciphertext, @Nonce, @KeyId, 'pending', @expires, @now, @now)
                    """, new { id, blogId, hash, sealedEmail.Ciphertext, sealedEmail.Nonce, sealedEmail.KeyId, expires, now });
            else
                // Waiting, or unsubscribed earlier and back: the address is sealed again and a new link goes out.
                await tx.Exec("""
                    UPDATE subscriber SET email_ciphertext = @Ciphertext, email_nonce = @Nonce, email_key_id = @KeyId, state = 'pending',
                        confirm_expires_at = @expires, confirm_sent_at = @now, unsubscribed_at = NULL
                    WHERE id = @id
                    """, new { id, sealedEmail.Ciphertext, sealedEmail.Nonce, sealedEmail.KeyId, expires, now });
            await tx.Exec("""
                INSERT INTO mail_out (id, blog_id, subscriber_id, kind, next_at, created_at) VALUES (@mail, @blogId, @id, 'confirm', @now, @now)
                """, new { mail = Guid.CreateVersion7(), blogId, id, now });
            await tx.Event(new EventRow(blogId, "subscriber", id, "subscriber_pending", Actors.System, null), now);
            return Outcome.Sent;
        }, ct);
    }

    private sealed record Link(Guid Id, string State, DateTime? ConfirmExpiresAt);

    /// <summary>Whether a confirmation link is good now (without using it): the page shows Confirm only then.</summary>
    public async Task<bool> ConfirmableAsync(Guid blogId, Guid id, string token, CancellationToken ct)
    {
        if (await KeyAsync(blogId, ct) is not { } key) return false;
        var s = await db.ReadAsync(tx => tx.One<Link>("SELECT id, state, confirm_expires_at FROM subscriber WHERE blog_id = @blogId AND id = @id",
            new { blogId, id }), ct);
        return s is { State: "pending", ConfirmExpiresAt: { } expires } && expires > clock.GetUtcNow().UtcDateTime
               && Same(token, Token(key, "confirm", id, expires));
    }

    public async Task<bool> ConfirmAsync(Guid blogId, Guid id, string token, CancellationToken ct)
    {
        if (!await ConfirmableAsync(blogId, id, token, ct)) return false;
        var now = clock.GetUtcNow();
        return await db.WriteAsync(async tx =>
        {
            if (await tx.Exec("""
                UPDATE subscriber SET state = 'confirmed', confirmed_at = @now, confirm_expires_at = NULL WHERE id = @id AND state = 'pending'
                """, new { id, now }) == 0) return false;
            await tx.Event(new EventRow(blogId, "subscriber", id, "subscriber_confirmed", Actors.System, null), now);
            return true;
        }, ct);
    }

    public async Task<bool> UnsubscribeTokenValidAsync(Guid blogId, Guid id, string token, CancellationToken ct) =>
        await KeyAsync(blogId, ct) is { } key && Same(token, Token(key, "unsubscribe", id));

    /// <summary>Unsubscribes and erases the address. True also when it was already gone: the link always ends well.</summary>
    public async Task<bool> UnsubscribeAsync(Guid blogId, Guid id, string token, CancellationToken ct)
    {
        if (!await UnsubscribeTokenValidAsync(blogId, id, token, ct)) return false;
        await EraseAsync(blogId, id, Actors.System, "subscriber_unsubscribed", ct);
        return true;
    }

    private Task EraseAsync(Guid blogId, Guid id, string actor, string kind, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.WriteAsync(async tx =>
        {
            if (await tx.Exec("""
                UPDATE subscriber SET state = 'unsubscribed', unsubscribed_at = @now, email_ciphertext = NULL, email_nonce = NULL, email_key_id = NULL,
                    confirm_expires_at = NULL
                WHERE blog_id = @blogId AND id = @id AND state <> 'unsubscribed'
                """, new { blogId, id, now }) == 0) return 0;
            await tx.Exec("""
                UPDATE mail_out SET status = 'skipped', error = 'Unsubscribed.' WHERE subscriber_id = @id AND status = 'pending'
                """, new { id });
            await tx.Event(new EventRow(blogId, "subscriber", id, kind, actor, null), now);
            return 1;
        }, ct);
    }

    /// <summary>The owner removes a subscriber: the same as their own unsubscribe, address erased.</summary>
    public Task RemoveAsync(Actor owner, Guid id, CancellationToken ct) => EraseAsync(owner.BlogId, id, owner.Value, "subscriber_removed", ct);

    public sealed record Listed(Guid Id, string? Email, string State, DateTime CreatedAt, DateTime? ConfirmedAt);

    private sealed record Row(Guid Id, byte[]? EmailCiphertext, byte[]? EmailNonce, string? EmailKeyId, string State, DateTime CreatedAt, DateTime? ConfirmedAt);

    /// <summary>The owner's list: confirmed and waiting, newest first, each address opened.</summary>
    public async Task<IReadOnlyList<Listed>> ListAsync(Guid blogId, CancellationToken ct)
    {
        var rows = await db.ReadAsync(tx => tx.Query<Row>("""
            SELECT id, email_ciphertext, email_nonce, email_key_id, state, created_at, confirmed_at
            FROM subscriber WHERE blog_id = @blogId AND state <> 'unsubscribed' ORDER BY created_at DESC LIMIT 5000
            """, new { blogId }), ct);
        return [.. rows.Select(r => new Listed(r.Id, Open(blogId, r.Id, r.EmailCiphertext, r.EmailNonce, r.EmailKeyId), r.State, r.CreatedAt, r.ConfirmedAt))];
    }

    /// <summary>Every confirmed subscriber, each address opened (for the export: all of them, no cap); those that don't open are left out.</summary>
    public async Task<IReadOnlyList<Listed>> ConfirmedAsync(Guid blogId, CancellationToken ct)
    {
        var rows = await db.ReadAsync(tx => tx.Query<Row>("""
            SELECT id, email_ciphertext, email_nonce, email_key_id, state, created_at, confirmed_at
            FROM subscriber WHERE blog_id = @blogId AND state = 'confirmed' ORDER BY created_at, id
            """, new { blogId }), ct);
        return [.. rows.Select(r => new Listed(r.Id, Open(blogId, r.Id, r.EmailCiphertext, r.EmailNonce, r.EmailKeyId), r.State, r.CreatedAt, r.ConfirmedAt))
            .Where(l => l.Email is not null)];
    }

    public Task<(int Confirmed, int Pending)> CountsAsync(Guid blogId, CancellationToken ct) => db.ReadAsync(async tx =>
        await tx.One<(int, int)>("""
            SELECT count(*) FILTER (WHERE state = 'confirmed')::int, count(*) FILTER (WHERE state = 'pending')::int FROM subscriber WHERE blog_id = @blogId
            """, new { blogId }), ct);

    /// <summary>A subscriber's address, for the queue, or null when it is erased or doesn't open.</summary>
    public async Task<(string State, string? Email)?> AddressAsync(Guid blogId, Guid id, CancellationToken ct)
    {
        var r = await db.ReadAsync(tx => tx.One<Row>("""
            SELECT id, email_ciphertext, email_nonce, email_key_id, state, created_at, confirmed_at FROM subscriber WHERE blog_id = @blogId AND id = @id
            """, new { blogId, id }), ct);
        return r is null ? null : (r.State, Open(blogId, id, r.EmailCiphertext, r.EmailNonce, r.EmailKeyId));
    }

    private string? Open(Guid blogId, Guid id, byte[]? ciphertext, byte[]? nonce, string? keyId) =>
        ciphertext is null || nonce is null || keyId is null ? null : sealer.OpenText(new SealedValue(ciphertext, nonce, keyId), ForSubscriber(blogId, id));

    public Task<DateTime?> ConfirmExpiresAsync(Guid id, CancellationToken ct) =>
        db.ReadAsync(tx => tx.One<DateTime?>("SELECT confirm_expires_at FROM subscriber WHERE id = @id", new { id }), ct);
}

/// <summary>
/// A per-address limit on the subscribe form (5 a minute unless set), in this process's memory: the form can't be used to flood
/// someone's inbox. Each address's window is forgotten once it ends.
/// </summary>
public sealed class SubscribeLimit(IConfiguration config)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Start, int Count)> _windows = new();

    /// <summary><c>Blog__Subscribe__PerMinute</c>, 5 unless set.</summary>
    public int PerMinute { get; } = Math.Max(1, config.GetValue("Blog:Subscribe:PerMinute", 5));

    public bool Allow(string client, DateTimeOffset now)
    {
        if (_windows.Count > 10_000)
            foreach (var old in _windows.Where(w => now - w.Value.Start > TimeSpan.FromMinutes(1)).Select(w => w.Key).ToList()) _windows.TryRemove(old, out _);
        var w = _windows.AddOrUpdate(client, _ => (now, 1), (_, v) => now - v.Start > TimeSpan.FromMinutes(1) ? (now, 1) : (v.Start, v.Count + 1));
        return w.Count <= PerMinute;
    }
}
