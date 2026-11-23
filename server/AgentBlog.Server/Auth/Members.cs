using System.Security.Cryptography;
using AgentBlog.Database;
using AgentBlog.Posts;
using AgentKit.ProviderSignIn;

namespace AgentBlog.Auth;

/// <summary>The people roles on a blog (docs/specs/members.md).</summary>
public static class Roles
{
    public const string Owner = "owner";
    public const string Editor = "editor";
    public const string Writer = "writer";

    public static readonly string[] Invitable = [Editor, Writer];
}

/// <summary>
/// Who is on a blog besides its owner, and how they got there (docs/specs/members.md): the owner's invitations (a link
/// that works once, for one account, for a week), the people who accepted, their roles, and removal. Only the owner
/// changes any of it; every change writes an event.
/// </summary>
public sealed class Members(Db db, TimeProvider clock)
{
    public static readonly TimeSpan InvitationLasts = TimeSpan.FromDays(7);
    public const int MaxPending = 50;

    /// <summary>The account's role on the blog, or null when it isn't on it.</summary>
    public Task<string?> RoleAsync(Guid blogId, Guid accountId, CancellationToken ct) => db.ReadAsync(tx => tx.One<string?>(
        "SELECT role FROM blog_member WHERE blog_id = @blogId AND account_id = @accountId", new { blogId, accountId }), ct);

    /// <summary>The account as a person on the blog of <paramref name="postId"/>, or null when it isn't on that blog.</summary>
    public Task<Actor?> OnPostAsync(Guid postId, Guid accountId, CancellationToken ct) => db.ReadAsync(async tx =>
    {
        var m = await tx.One<Membership>("""
            SELECT p.blog_id, m.role FROM post p JOIN blog_member m ON m.blog_id = p.blog_id AND m.account_id = @accountId WHERE p.id = @postId
            """, new { postId, accountId });
        return m is null ? null : Actor.Person(m.BlogId, accountId, m.Role);
    }, ct);

    private sealed record Membership(Guid BlogId, string Role);

    public async Task<Actor?> OnBlogAsync(Guid blogId, Guid accountId, CancellationToken ct) =>
        await RoleAsync(blogId, accountId, ct) is { } role ? Actor.Person(blogId, accountId, role) : null;

    public sealed record Person(Guid AccountId, string DisplayName, string Role, DateTime JoinedAt, string? AuthorSlug);

    public Task<IReadOnlyList<Person>> PeopleAsync(Guid blogId, CancellationToken ct) => db.ReadAsync<IReadOnlyList<Person>>(async tx =>
        [.. await tx.Query<Person>("""
            SELECT m.account_id, a.display_name, m.role, m.created_at AS joined_at, au.slug AS author_slug
            FROM blog_member m JOIN account a ON a.id = m.account_id
            LEFT JOIN author au ON au.blog_id = m.blog_id AND au.account_id = m.account_id
            WHERE m.blog_id = @blogId ORDER BY CASE m.role WHEN 'owner' THEN 0 WHEN 'editor' THEN 1 ELSE 2 END, m.created_at
            """, new { blogId })], ct);

    public sealed record Invitation(Guid Id, string Role, string Note, DateTime CreatedAt, DateTime ExpiresAt, DateTime? UsedAt,
        string? UsedByName, DateTime? WithdrawnAt)
    {
        public string State(DateTime now) => WithdrawnAt is not null ? "withdrawn" : UsedAt is not null ? "accepted" : ExpiresAt <= now ? "expired" : "pending";
    }

    public Task<IReadOnlyList<Invitation>> InvitationsAsync(Guid blogId, CancellationToken ct) => db.ReadAsync<IReadOnlyList<Invitation>>(async tx =>
        [.. await tx.Query<Invitation>("""
            SELECT i.id, i.role, i.note, i.created_at, i.expires_at, i.used_at, a.display_name AS used_by_name, i.withdrawn_at
            FROM invitation i LEFT JOIN account a ON a.id = i.used_by
            WHERE i.blog_id = @blogId ORDER BY i.created_at DESC LIMIT 100
            """, new { blogId })], ct);

    public static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    /// <summary>
    /// A new invitation as <paramref name="role"/>: returns its token (for the link, shown once; only its hash is kept), or
    /// why not.
    /// </summary>
    public async Task<(Invitation? Invitation, string? Token, string? Error)> InviteAsync(Actor owner, string role, string? note, CancellationToken ct)
    {
        if (!owner.IsOwner) return (null, null, "Only the owner invites.");
        if (!Roles.Invitable.Contains(role)) return (null, null, "An invitation is for a writer or an editor.");
        note = string.Join(' ', (note ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (note.Length > 200) return (null, null, "A note is at most 200 characters.");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        var made = await db.WriteAsync(async tx =>
        {
            if (await tx.One<int>("""
                SELECT count(*)::int FROM invitation WHERE blog_id = @BlogId AND used_at IS NULL AND withdrawn_at IS NULL AND expires_at > @now
                """, new { owner.BlogId, now }) >= MaxPending) return false;
            await tx.Exec("""
                INSERT INTO invitation (id, blog_id, role, token_hash, note, created_by, created_at, expires_at)
                VALUES (@id, @BlogId, @role, @hash, @note, @AccountId, @now, @expires)
                """, new { id, owner.BlogId, role, hash = Hash(token), note, owner.AccountId, now, expires = now + InvitationLasts });
            await tx.Event(new EventRow(owner.BlogId, "invitation", id, "invitation_created", owner.Value, new { role }), now);
            return true;
        }, ct);
        if (!made) return (null, null, $"At most {MaxPending} invitations wait at once: withdraw some first.");
        return ((await InvitationsAsync(owner.BlogId, ct)).First(i => i.Id == id), token, null);
    }

    public Task<bool> WithdrawAsync(Actor owner, Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.WriteAsync(async tx =>
        {
            if (!owner.IsOwner || await tx.Exec("""
                UPDATE invitation SET withdrawn_at = @now WHERE blog_id = @BlogId AND id = @id AND used_at IS NULL AND withdrawn_at IS NULL
                """, new { owner.BlogId, id, now }) == 0) return false;
            await tx.Event(new EventRow(owner.BlogId, "invitation", id, "invitation_withdrawn", owner.Value, null), now);
            return true;
        }, ct);
    }

    public sealed record Pending(Guid Id, Guid BlogId, string BlogTitle, string Role, string InvitedBy, DateTime ExpiresAt);

    /// <summary>What an invitation link offers, while it still works: for its page.</summary>
    public Task<Pending?> LookUpAsync(string token, CancellationToken ct) => db.ReadAsync(tx => tx.One<Pending>("""
        SELECT i.id, i.blog_id, b.title AS blog_title, i.role, a.display_name AS invited_by, i.expires_at
        FROM invitation i JOIN blog b ON b.id = i.blog_id JOIN account a ON a.id = i.created_by
        WHERE i.token_hash = @hash AND i.used_at IS NULL AND i.withdrawn_at IS NULL AND i.expires_at > @now
        """, new { hash = Hash(token), now = clock.GetUtcNow() }), ct);

    public enum Accepted { Joined, Gone, AlreadyOn }

    private sealed record Locked(Guid Id, Guid BlogId, string Role, DateTime ExpiresAt, DateTime? UsedAt, DateTime? WithdrawnAt);

    /// <summary>
    /// The signed-in provider account accepts the invitation whose token hashes to <paramref name="tokenHash"/>: it joins
    /// the blog in the invitation's role (a new account, named from the provider, if it is new here), and the invitation
    /// is spent. Atomic: two people racing on one link can't both join.
    /// </summary>
    public Task<(Accepted Outcome, Guid? AccountId)> AcceptAsync(byte[] tokenHash, ProviderIdentity identity, CancellationToken ct) =>
        db.WriteAsync<(Accepted, Guid?)>(async tx =>
        {
            var now = clock.GetUtcNow();
            var i = await tx.One<Locked>("""
                SELECT id, blog_id, role, expires_at, used_at, withdrawn_at FROM invitation WHERE token_hash = @tokenHash FOR UPDATE
                """, new { tokenHash });
            if (i is null || i.UsedAt is not null || i.WithdrawnAt is not null || i.ExpiresAt <= now.UtcDateTime) return (Accepted.Gone, null);

            var accountId = await tx.One<Guid?>(
                "SELECT account_id FROM account_identity WHERE provider = @Provider AND provider_account_id = @AccountId", identity);
            if (accountId is { } known && await tx.One<bool>(
                    "SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @BlogId AND account_id = @known)", new { i.BlogId, known }))
                return (Accepted.AlreadyOn, known); // the link stays unspent: it's for someone else
            if (accountId is null)
            {
                accountId = Guid.CreateVersion7();
                var name = string.IsNullOrWhiteSpace(identity.DisplayName) ? "A writer" : identity.DisplayName.Trim()[..Math.Min(100, identity.DisplayName.Trim().Length)];
                await tx.Exec("INSERT INTO account (id, display_name, created_at) VALUES (@accountId, @name, @now)", new { accountId, name, now });
                await tx.Exec("""
                    INSERT INTO account_identity (provider, provider_account_id, account_id, display_name, created_at, last_used_at)
                    VALUES (@Provider, @ProviderAccountId, @accountId, @DisplayName, @now, @now)
                    """, new { identity.Provider, ProviderAccountId = identity.AccountId, accountId, identity.DisplayName, now });
                await tx.Event(new EventRow(null, "account", accountId.Value, "account_created", Actors.Account(accountId.Value), new { via = "invitation" }), now);
            }
            await tx.Exec("INSERT INTO blog_member (blog_id, account_id, role, created_at) VALUES (@BlogId, @accountId, @Role, @now)",
                new { i.BlogId, accountId, i.Role, now });
            await tx.Exec("UPDATE invitation SET used_at = @now, used_by = @accountId WHERE id = @Id", new { i.Id, accountId, now });
            await tx.Event(new EventRow(i.BlogId, "invitation", i.Id, "invitation_accepted", Actors.Account(accountId.Value), new { role = i.Role }), now);
            return (Accepted.Joined, accountId);
        }, ct);

    /// <summary>The owner makes someone an editor or a writer. The owner's own role never changes here.</summary>
    public Task<bool> SetRoleAsync(Actor owner, Guid accountId, string role, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.WriteAsync(async tx =>
        {
            if (!owner.IsOwner || !Roles.Invitable.Contains(role)) return false;
            var before = await tx.One<string?>("SELECT role FROM blog_member WHERE blog_id = @BlogId AND account_id = @accountId FOR UPDATE",
                new { owner.BlogId, accountId });
            if (before is null or Roles.Owner) return false;
            if (before == role) return true;
            await tx.Exec("UPDATE blog_member SET role = @role WHERE blog_id = @BlogId AND account_id = @accountId", new { owner.BlogId, accountId, role });
            await tx.Event(new EventRow(owner.BlogId, "account", accountId, "member_role_changed", owner.Value, new { from = before, to = role }), now);
            return true;
        }, ct);
    }

    /// <summary>
    /// The owner removes someone: their access ends at once, and their agents' on this blog (revoked; the returned grants
    /// are for their tokens to be revoked too); everything they and their agents wrote stays under their names. Null when
    /// there was nobody to remove.
    /// </summary>
    public Task<IReadOnlyList<string>?> RemoveAsync(Actor owner, Guid accountId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.WriteAsync<IReadOnlyList<string>?>(async tx =>
        {
            if (!owner.IsOwner) return null;
            var role = await tx.One<string?>("""
                DELETE FROM blog_member WHERE blog_id = @BlogId AND account_id = @accountId AND role <> 'owner' RETURNING role
                """, new { owner.BlogId, accountId });
            if (role is null) return null;
            var grants = await AgentBlog.Connections.ConnectionStore.RevokeAllOfAsync(tx, owner.BlogId, accountId, owner.Value, now);
            // Signed in nowhere else, their sessions end now: the next request is a sign-in page, not an empty app.
            await tx.Exec("""
                DELETE FROM account_session s WHERE s.account_id = @accountId AND NOT EXISTS (SELECT 1 FROM blog_member m WHERE m.account_id = @accountId)
                """, new { accountId });
            await tx.Event(new EventRow(owner.BlogId, "account", accountId, "member_removed", owner.Value, new { role, agents_revoked = grants.Count }), now);
            return grants;
        }, ct);
    }
}
