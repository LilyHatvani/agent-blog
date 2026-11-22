using AgentBlog.Database;

namespace AgentBlog.Connections;

/// <summary>The autonomy levels (decision 0001). Propose is the default; Full is given only with the owner's confirmation.</summary>
public static class Levels
{
    public const string Propose = "propose";
    public const string Publish = "publish";
    public const string Full = "full";

    public static readonly string[] All = [Propose, Publish, Full];

    public static int Rank(string level) => Array.IndexOf(All, level);

    /// <summary>What a level lets the agent do, in the owner's words. Shown at consent, in the list, and to the agent.</summary>
    public static string Means(string level) => level switch
    {
        Propose => "It writes drafts and suggests; publishing, scheduling and trashing wait for you.",
        Publish => "It publishes and schedules on its own; trashing still waits for you.",
        Full => "It publishes, changes and trashes posts without asking you. You can see and undo everything it does in History.",
        _ => "",
    };
}

/// <summary>
/// Agent connections (docs/specs/connections.md): one OAuth grant, one blog, one level. The owner creates one by approving
/// a client on the consent page, and is the only one who changes its level or revokes it. No agent code path writes here
/// except the per-call <see cref="ResolveAsync"/>, which only reads (and notes when it was last used).
/// </summary>
public sealed class ConnectionStore(Db db, TimeProvider clock)
{
    public sealed record Connection(Guid Id, Guid BlogId, string BlogTitle, string Label, string Level, string? ClientId,
        DateTime CreatedAt, DateTime? LastUsedAt);

    /// <summary>What an MCP call runs as: resolved fresh on every call.</summary>
    public sealed record Caller(Guid ConnectionId, Guid BlogId, string Level);

    public enum Outcome { Done, NotFound, NeedsConfirmation, Invalid }

    /// <summary>The blogs this account owns: the only ones a connection can be made for.</summary>
    public Task<IReadOnlyList<(Guid Id, string Title)>> OwnedBlogsAsync(Guid accountId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<(Guid, string)>>(async tx => [.. (await tx.Query<(Guid, string)>("""
            SELECT b.id, b.title FROM blog_member m JOIN blog b ON b.id = m.blog_id
            WHERE m.account_id = @accountId AND m.role = 'owner' ORDER BY m.created_at
            """, new { accountId }))], ct);

    /// <summary>
    /// Creates the connection behind a grant the owner just approved, at Propose. Refused (false) when the account doesn't
    /// own the blog: a forged form can't attach a connection to someone else's blog.
    /// </summary>
    public Task<bool> CreateAsync(Guid connectionId, Guid accountId, Guid blogId, string label, string clientId,
        string authorizationId, CancellationToken ct) => db.WriteAsync(async tx =>
    {
        var owns = await tx.One<bool>("""
            SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @blogId AND account_id = @accountId AND role = 'owner')
            """, new { blogId, accountId });
        if (!owns) return false;
        var now = clock.GetUtcNow();
        await tx.Exec("""
            INSERT INTO connection (id, blog_id, label, level, oauth_authorization_id, client_id, created_by, created_at)
            VALUES (@connectionId, @blogId, @label, 'propose', @authorizationId, @clientId, @accountId, @now)
            """, new { connectionId, blogId, label, authorizationId, clientId, accountId, now });
        await tx.Event(new EventRow(blogId, "connection", connectionId, "connection_created", Actors.Account(accountId),
            new { label, client_id = clientId, level = Levels.Propose }), now);
        return true;
    }, ct);

    /// <summary>The live connection behind a grant, or null (revoked, or not this grant's). Read on every MCP call, uncached.</summary>
    public Task<Caller?> ResolveAsync(Guid connectionId, string authorizationId, CancellationToken ct) => db.OperationalAsync(async tx =>
    {
        var caller = await tx.One<Caller>("""
            SELECT id AS connection_id, blog_id, level FROM connection
            WHERE id = @connectionId AND oauth_authorization_id = @authorizationId AND revoked_at IS NULL
            """, new { connectionId, authorizationId });
        if (caller is not null)
        {
            // At most one write a minute per connection, for the owner's "last used".
            await tx.Exec("""
                UPDATE connection SET last_used_at = @now
                WHERE id = @connectionId AND (last_used_at IS NULL OR last_used_at < @now - interval '1 minute')
                """, new { connectionId, now = clock.GetUtcNow() });
        }
        return caller;
    }, ct);

    public Task<IReadOnlyList<Connection>> ListAsync(Guid accountId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<Connection>>(async tx => [.. await tx.Query<Connection>("""
            SELECT c.id, c.blog_id, b.title AS blog_title, c.label, c.level, c.client_id, c.created_at, c.last_used_at
            FROM connection c
            JOIN blog b ON b.id = c.blog_id
            JOIN blog_member m ON m.blog_id = c.blog_id AND m.account_id = @accountId AND m.role = 'owner'
            WHERE c.revoked_at IS NULL
            ORDER BY c.created_at
            """, new { accountId })], ct);

    /// <summary>
    /// Sets a connection's level. Raising one to Full needs <paramref name="confirmedFull"/>: the owner was told, in plain
    /// words, what Full means (decision 0001). Only an owner of the connection's blog reaches it.
    /// </summary>
    public Task<Outcome> SetLevelAsync(Guid accountId, Guid connectionId, string level, bool confirmedFull, CancellationToken ct) =>
        db.WriteAsync(async tx =>
        {
            if (!Levels.All.Contains(level)) return Outcome.Invalid;
            var current = await OwnedAsync(tx, accountId, connectionId);
            if (current is null) return Outcome.NotFound;
            if (current.Level == level) return Outcome.Done;
            if (level == Levels.Full && !confirmedFull) return Outcome.NeedsConfirmation;
            await tx.Exec("UPDATE connection SET level = @level WHERE id = @connectionId", new { level, connectionId });
            await tx.Event(new EventRow(current.BlogId, "connection", connectionId, "connection_level_changed",
                Actors.Account(accountId), new { from = current.Level, to = level }), clock.GetUtcNow());
            return Outcome.Done;
        }, ct);

    /// <summary>Revokes a connection. Returns its OAuth authorization id, for the caller to revoke the grant's tokens too.</summary>
    public Task<(Outcome, string?)> RevokeAsync(Guid accountId, Guid connectionId, CancellationToken ct) =>
        db.WriteAsync<(Outcome, string?)>(async tx =>
        {
            var current = await OwnedAsync(tx, accountId, connectionId);
            if (current is null) return (Outcome.NotFound, null);
            var authorization = await tx.One<string>("""
                UPDATE connection SET revoked_at = @now WHERE id = @connectionId RETURNING oauth_authorization_id
                """, new { connectionId, now = clock.GetUtcNow() });
            await tx.Event(new EventRow(current.BlogId, "connection", connectionId, "connection_revoked",
                Actors.Account(accountId), new { }), clock.GetUtcNow());
            return (Outcome.Done, authorization);
        }, ct);

    private sealed record Owned(Guid BlogId, string Level);

    private static Task<Owned?> OwnedAsync(Tx tx, Guid accountId, Guid connectionId) => tx.One<Owned>("""
        SELECT c.blog_id, c.level FROM connection c
        JOIN blog_member m ON m.blog_id = c.blog_id AND m.account_id = @accountId AND m.role = 'owner'
        WHERE c.id = @connectionId AND c.revoked_at IS NULL
        FOR UPDATE OF c
        """, new { accountId, connectionId });
}
