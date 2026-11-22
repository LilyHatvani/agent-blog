using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace AgentBlog.Auth;

/// <summary>
/// Signed-in browsers (docs/specs/auth.md § Sessions): a random cookie value, of which only the SHA-256 is stored, good for
/// 30 days and pushed out at most once a day while it's used. Shaped after Relay's <c>Api/OwnerSessions.cs</c>, keyed
/// by account (decision 0005: many accounts on a hosted instance).
/// </summary>
public sealed class Sessions(NpgsqlDataSource data, TimeProvider clock, IConfiguration config)
{
    public const string Cookie = "blog_session";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan RenewAfter = TimeSpan.FromDays(1);

    /// <summary>A live session: its id (the hex SHA-256 of the cookie), whose it is, and the identity that began it.</summary>
    public sealed record Session(string Id, Guid AccountId, string Provider, string ProviderAccountId);

    public async Task<Session> StartAsync(HttpContext ctx, Guid accountId, string provider, string providerAccountId, CancellationToken ct)
    {
        var value = Base64Url(RandomNumberGenerator.GetBytes(32));
        var hash = Hash(value);
        var now = clock.GetUtcNow();
        await using var conn = await data.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM account_session WHERE expires_at <= @now;
            INSERT INTO account_session (id_hash, account_id, provider, provider_account_id, created_at, last_seen_at, expires_at)
            VALUES (@hash, @accountId, @provider, @providerAccountId, @now, @now, @expires)
            """, new { hash, accountId, provider, providerAccountId, now, expires = now + Lifetime }, cancellationToken: ct));
        ctx.Response.Cookies.Append(Cookie, value, Options(Lifetime));
        return new Session(Convert.ToHexStringLower(hash), accountId, provider, providerAccountId);
    }

    /// <summary>The live session behind this request's cookie (renewed when a day has passed), or null.</summary>
    public async Task<Session?> AuthenticateAsync(HttpContext ctx, CancellationToken ct)
    {
        if (ctx.Items.TryGetValue(typeof(Session), out var cached)) return (Session?)cached;
        var session = await ReadAsync(ctx, ct);
        ctx.Items[typeof(Session)] = session;
        return session;
    }

    private async Task<Session?> ReadAsync(HttpContext ctx, CancellationToken ct)
    {
        var value = ctx.Request.Cookies[Cookie];
        if (value is not { Length: > 0 and <= 100 }) return null;
        var hash = Hash(value);
        var now = clock.GetUtcNow();
        await using var conn = await data.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition("""
            SELECT account_id, provider, provider_account_id, expires_at FROM account_session
            WHERE id_hash = @hash AND expires_at > @now
            """, new { hash, now }, cancellationToken: ct));
        if (row is null) return null;

        if (new DateTimeOffset(row.ExpiresAt, TimeSpan.Zero) - now < Lifetime - RenewAfter && !ctx.Response.HasStarted)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE account_session SET last_seen_at = @now, expires_at = @expires WHERE id_hash = @hash",
                new { hash, now, expires = now + Lifetime }, cancellationToken: ct));
            ctx.Response.Cookies.Append(Cookie, value, Options(Lifetime));
        }
        return new Session(Convert.ToHexStringLower(hash), row.AccountId, row.Provider, row.ProviderAccountId);
    }

    /// <summary>Whether the session with this id is still live (a bind begun by it is good only while it is).</summary>
    public async Task<bool> IsLiveAsync(string id, Guid accountId, CancellationToken ct)
    {
        byte[] hash;
        try { hash = Convert.FromHexString(id); }
        catch (FormatException) { return false; }
        await using var conn = await data.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM account_session WHERE id_hash = @hash AND account_id = @accountId AND expires_at > @now)
            """, new { hash, accountId, now = clock.GetUtcNow() }, cancellationToken: ct));
    }

    /// <summary>Sign out: ends this browser's session and clears its cookie.</summary>
    public async Task EndAsync(HttpContext ctx, CancellationToken ct)
    {
        if (ctx.Request.Cookies[Cookie] is { Length: > 0 } value)
        {
            await using var conn = await data.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM account_session WHERE id_hash = @hash",
                new { hash = Hash(value) }, cancellationToken: ct));
        }
        ctx.Response.Cookies.Delete(Cookie, Options(null));
    }

    /// <summary>Sign out everywhere: every session of this account, on every device.</summary>
    public async Task<int> EndAllAsync(HttpContext ctx, Guid accountId, CancellationToken ct)
    {
        await using var conn = await data.OpenConnectionAsync(ct);
        var ended = await conn.ExecuteAsync(new CommandDefinition("DELETE FROM account_session WHERE account_id = @accountId",
            new { accountId }, cancellationToken: ct));
        ctx.Response.Cookies.Delete(Cookie, Options(null));
        return ended;
    }

    /// <summary>
    /// Secure follows the configured public origin, not the request: TLS ends at the platform's proxy (Railway), so the
    /// request arrives as http. Only an http public origin (local development) drops it.
    /// </summary>
    private CookieOptions Options(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = !(Uri.TryCreate(config["Blog:PublicUrl"], UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp),
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = maxAge,
        IsEssential = true,
    };

    internal static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class Row
    {
        public Guid AccountId { get; init; }
        public string Provider { get; init; } = "";
        public string ProviderAccountId { get; init; } = "";
        public DateTime ExpiresAt { get; init; }
    }
}
