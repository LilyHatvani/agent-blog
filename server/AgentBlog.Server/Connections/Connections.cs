using AgentBlog.Auth;
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

    /// <summary>
    /// The highest level an agent may work at for a person in <paramref name="role"/> (docs/specs/members.md): it never
    /// does more than its person may. The owner's up to Full, an editor's up to Publish, a writer's at Propose.
    /// </summary>
    public static string CapFor(string role) => role switch
    {
        Roles.Owner => Full,
        Roles.Editor => Publish,
        _ => Propose,
    };

    /// <summary>The lower of two levels.</summary>
    public static string Lower(string a, string b) => Rank(a) <= Rank(b) ? a : b;

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
/// Agent connections (docs/specs/connections.md): one OAuth grant, one blog, one level, one person. Anyone on the blog
/// connects their own agent on the consent page (docs/specs/members.md); the owner alone sets levels, never above what
/// the agent's person may do; the person or the owner revokes it. The level an agent works at is judged on every call:
/// the lower of its level and its person's cap, and nothing once its person is off the blog. No agent code path writes
/// here except the per-call <see cref="ResolveAsync"/>, which only reads (and notes when it was last used).
/// </summary>
public sealed class ConnectionStore(Db db, TimeProvider clock)
{
    public sealed record Connection(Guid Id, Guid BlogId, string BlogTitle, string Label, string Level, string? ClientId,
        DateTime CreatedAt, DateTime? LastUsedAt, string BylineName, bool BylineNameSet, Guid PersonId, string PersonName,
        string? PersonRole, bool Mine, bool ViewerOwns)
    {
        /// <summary>The most its person lets it do; nothing when they are off the blog.</summary>
        public string? Cap => PersonRole is { } r ? Levels.CapFor(r) : null;

        /// <summary>The level it works at now.</summary>
        public string? WorksAt => Cap is { } c ? Levels.Lower(Level, c) : null;
    }

    /// <summary>
    /// What an MCP call runs as, resolved fresh on every call: the level it works at (its own, held to its person's cap),
    /// and the person whose agent it is, with their role.
    /// </summary>
    public sealed record Caller(Guid ConnectionId, Guid BlogId, string Level, Guid PersonId, string Role);

    public enum Outcome { Done, NotFound, NeedsConfirmation, Invalid, AboveCap }

    /// <summary>The blogs this account is on, with its role: the ones it can connect an agent to.</summary>
    public Task<IReadOnlyList<(Guid Id, string Title, string Role)>> MemberBlogsAsync(Guid accountId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<(Guid, string, string)>>(async tx => [.. (await tx.Query<(Guid, string, string)>("""
            SELECT b.id, b.title, m.role FROM blog_member m JOIN blog b ON b.id = m.blog_id
            WHERE m.account_id = @accountId ORDER BY m.created_at
            """, new { accountId }))], ct);

    /// <summary>A name fit for a byline: one line, at most 60 characters; null when there is nothing left.</summary>
    public static string? BylineName(string? name)
    {
        var one = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return one.Length == 0 ? null : one.Length <= 60 ? one : one[..60].TrimEnd();
    }

    /// <summary>
    /// Creates the connection behind a grant a person on the blog just approved, at Propose, as their agent. Refused
    /// (false) when the account isn't on the blog: a forged form can't attach a connection to someone else's blog.
    /// </summary>
    /// <param name="bylineName">The name the client gave itself, its default name on posts (docs/specs/bylines.md); the owner can change it.</param>
    public Task<bool> CreateAsync(Guid connectionId, Guid accountId, Guid blogId, string label, string clientId,
        string authorizationId, CancellationToken ct, string? bylineName = null) => db.WriteAsync(async tx =>
    {
        var member = await tx.One<bool>("""
            SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @blogId AND account_id = @accountId)
            """, new { blogId, accountId });
        if (!member) return false;
        var now = clock.GetUtcNow();
        await tx.Exec("""
            INSERT INTO connection (id, blog_id, label, level, oauth_authorization_id, client_id, byline_name, created_by, created_at)
            VALUES (@connectionId, @blogId, @label, 'propose', @authorizationId, @clientId, @bylineName, @accountId, @now)
            """, new { connectionId, blogId, label, authorizationId, clientId, bylineName = BylineName(bylineName), accountId, now });
        await tx.Event(new EventRow(blogId, "connection", connectionId, "connection_created", Actors.Account(accountId),
            new { label, client_id = clientId, level = Levels.Propose }), now);
        return true;
    }, ct);

    /// <summary>The live connection behind a grant, or null (revoked, or not this grant's). Read on every MCP call, uncached.</summary>
    public Task<Caller?> ResolveAsync(Guid connectionId, string authorizationId, CancellationToken ct) => db.OperationalAsync(async tx =>
    {
        // Its person's role now: an agent whose person left the blog has no level at all.
        var row = await tx.One<Live>("""
            SELECT c.id AS connection_id, c.blog_id, c.level, c.created_by AS person_id, m.role FROM connection c
            JOIN blog_member m ON m.blog_id = c.blog_id AND m.account_id = c.created_by
            WHERE c.id = @connectionId AND c.oauth_authorization_id = @authorizationId AND c.revoked_at IS NULL
            """, new { connectionId, authorizationId });
        var caller = row is null ? null : new Caller(row.ConnectionId, row.BlogId, Levels.Lower(row.Level, Levels.CapFor(row.Role)), row.PersonId, row.Role);
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

    private sealed record Live(Guid ConnectionId, Guid BlogId, string Level, Guid PersonId, string Role);

    /// <summary>The connections this account sees: every one on a blog it owns, and its own agents elsewhere.</summary>
    public Task<IReadOnlyList<Connection>> ListAsync(Guid accountId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<Connection>>(async tx => [.. await tx.Query<Connection>("""
            SELECT c.id, c.blog_id, b.title AS blog_title, c.label, c.level, c.client_id, c.created_at, c.last_used_at,
                   coalesce(c.byline_name, nullif(split_part(c.label, ' · ', 1), ''), c.label) AS byline_name,
                   c.byline_name IS NOT NULL AS byline_name_set,
                   c.created_by AS person_id, a.display_name AS person_name, pm.role AS person_role,
                   c.created_by = @accountId AS mine, me.role = 'owner' AS viewer_owns
            FROM connection c
            JOIN blog b ON b.id = c.blog_id
            JOIN blog_member me ON me.blog_id = c.blog_id AND me.account_id = @accountId
            JOIN account a ON a.id = c.created_by
            LEFT JOIN blog_member pm ON pm.blog_id = c.blog_id AND pm.account_id = c.created_by
            WHERE c.revoked_at IS NULL AND (me.role = 'owner' OR c.created_by = @accountId)
            ORDER BY c.created_at
            """, new { accountId })], ct);

    /// <summary>
    /// Sets a connection's level. Raising one to Full needs <paramref name="confirmedFull"/>: the owner was told, in plain
    /// words, what Full means (decision 0001). Only an owner of the connection's blog reaches it, and never above what the
    /// agent's person may do (<see cref="Outcome.AboveCap"/>).
    /// </summary>
    public Task<Outcome> SetLevelAsync(Guid accountId, Guid connectionId, string level, bool confirmedFull, CancellationToken ct) =>
        db.WriteAsync(async tx =>
        {
            if (!Levels.All.Contains(level)) return Outcome.Invalid;
            var current = await OwnedAsync(tx, accountId, connectionId);
            if (current is null) return Outcome.NotFound;
            if (current.PersonRole is not { } role || Levels.Rank(level) > Levels.Rank(Levels.CapFor(role))) return Outcome.AboveCap;
            if (current.Level == level) return Outcome.Done;
            if (level == Levels.Full && !confirmedFull) return Outcome.NeedsConfirmation;
            await tx.Exec("UPDATE connection SET level = @level WHERE id = @connectionId", new { level, connectionId });
            await tx.Event(new EventRow(current.BlogId, "connection", connectionId, "connection_level_changed",
                Actors.Account(accountId), new { from = current.Level, to = level }), clock.GetUtcNow());
            return Outcome.Done;
        }, ct);

    /// <summary>
    /// Sets the name a connection's agent goes by on posts (docs/specs/bylines.md); empty goes back to the name its
    /// client gave. The owner, or the person whose agent it is.
    /// </summary>
    public Task<Outcome> SetBylineNameAsync(Guid accountId, Guid connectionId, string name, CancellationToken ct) =>
        db.WriteAsync(async tx =>
        {
            var current = await OwnedAsync(tx, accountId, connectionId, orMine: true);
            if (current is null) return Outcome.NotFound;
            var trimmed = name.Trim();
            if (trimmed.Length > 60 || trimmed.Any(char.IsControl)) return Outcome.Invalid;
            var value = BylineName(trimmed);
            await tx.Exec("UPDATE connection SET byline_name = @value WHERE id = @connectionId", new { value, connectionId });
            await tx.Event(new EventRow(current.BlogId, "connection", connectionId, "connection_byline_name_changed",
                Actors.Account(accountId), new { to = value }), clock.GetUtcNow());
            return Outcome.Done;
        }, ct);

    /// <summary>
    /// Revokes a connection: the owner, or the person whose agent it is. Returns its OAuth authorization id, for the
    /// caller to revoke the grant's tokens too.
    /// </summary>
    public Task<(Outcome, string?)> RevokeAsync(Guid accountId, Guid connectionId, CancellationToken ct) =>
        db.WriteAsync<(Outcome, string?)>(async tx =>
        {
            var current = await OwnedAsync(tx, accountId, connectionId, orMine: true);
            if (current is null) return (Outcome.NotFound, null);
            var authorization = await tx.One<string>("""
                UPDATE connection SET revoked_at = @now WHERE id = @connectionId RETURNING oauth_authorization_id
                """, new { connectionId, now = clock.GetUtcNow() });
            await tx.Event(new EventRow(current.BlogId, "connection", connectionId, "connection_revoked",
                Actors.Account(accountId), new { }), clock.GetUtcNow());
            return (Outcome.Done, authorization);
        }, ct);

    private sealed record Owned(Guid BlogId, string Level, string? PersonRole);

    /// <summary>A live connection the account may change: on a blog it owns, or (with <paramref name="orMine"/>) its own agent.</summary>
    private static Task<Owned?> OwnedAsync(Tx tx, Guid accountId, Guid connectionId, bool orMine = false) => tx.One<Owned>("""
        SELECT c.blog_id, c.level, pm.role AS person_role FROM connection c
        JOIN blog_member m ON m.blog_id = c.blog_id AND m.account_id = @accountId
        LEFT JOIN blog_member pm ON pm.blog_id = c.blog_id AND pm.account_id = c.created_by
        WHERE c.id = @connectionId AND c.revoked_at IS NULL AND (m.role = 'owner' OR (@orMine AND c.created_by = @accountId))
        FOR UPDATE OF c
        """, new { accountId, connectionId, orMine });

    /// <summary>
    /// Revokes every live connection of <paramref name="accountId"/> on the blog (they were removed from it), inside the
    /// caller's transaction. Returns the grants, for their tokens to be revoked too.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> RevokeAllOfAsync(Tx tx, Guid blogId, Guid accountId, string actor, DateTimeOffset now)
    {
        var gone = (await tx.Query<(Guid Id, string? Authorization)>("""
            UPDATE connection SET revoked_at = @now WHERE blog_id = @blogId AND created_by = @accountId AND revoked_at IS NULL
            RETURNING id, oauth_authorization_id
            """, new { blogId, accountId, now })).ToList();
        foreach (var (id, _) in gone)
            await tx.Event(new EventRow(blogId, "connection", id, "connection_revoked", actor, new { why = "person_removed" }), now);
        return [.. gone.Where(g => g.Authorization is not null).Select(g => g.Authorization!)];
    }
}
