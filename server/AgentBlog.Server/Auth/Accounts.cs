using AgentBlog.Database;
using AgentKit.ProviderSignIn;

namespace AgentBlog.Auth;

/// <summary>
/// Who a verified provider account is (docs/specs/auth.md). The package proves (provider, account id); this decides:
/// setup creates the owner and their blog, sign-in finds a bound account, and a signed-in owner binds another provider.
/// Matched by (provider, account id) only, never by email.
/// </summary>
public sealed class Accounts(Db db, TimeProvider clock, IConfiguration config)
{
    public sealed record Identity(string Provider, string ProviderAccountId, string? DisplayName, DateTime CreatedAt, DateTime? LastUsedAt);

    public sealed record BlogMembership(Guid Id, string Slug, string Title, string Role, string? AuthorSlug, string? AuthorProfileUrl);

    public sealed record Me(Guid Id, string DisplayName, IReadOnlyList<BlogMembership> Blogs);

    /// <summary>Whether setup is open for this token: never completed, or completed with a different token (re-opened).</summary>
    public Task<bool> SetupOpenAsync(byte[] tokenHash, CancellationToken ct) => db.ReadAsync(async tx =>
        await tx.One<byte[]>("SELECT burned_token_hash FROM instance_setup") is not { } burned
        || !burned.AsSpan().SequenceEqual(tokenHash), ct);

    /// <summary>
    /// Completes setup with <paramref name="identity"/>: the first time, creates the owner's account and the blog; after a
    /// re-open (a new setup token), binds the identity to the existing owner and ends that owner's sessions. Returns the
    /// owner's account, or null when setup closed while the sign-in was at the provider.
    /// </summary>
    public Task<Guid?> CompleteSetupAsync(ProviderIdentity identity, byte[] tokenHash, CancellationToken ct) => db.WriteAsync<Guid?>(async tx =>
    {
        var burned = await tx.One<byte[]>("SELECT burned_token_hash FROM instance_setup FOR UPDATE");
        if (burned is not null && burned.AsSpan().SequenceEqual(tokenHash)) return null;
        var now = clock.GetUtcNow();

        var owner = await tx.One<Guid?>("""
            SELECT account_id FROM account_identity WHERE provider = @Provider AND provider_account_id = @AccountId
            """, identity)
            ?? await tx.One<Guid?>("SELECT account_id FROM blog_member WHERE role = 'owner' ORDER BY created_at LIMIT 1");

        if (owner is not { } accountId)
        {
            accountId = Guid.CreateVersion7();
            var blogId = Guid.CreateVersion7();
            var slug = config["Blog:Slug"] is { Length: > 0 } s ? s : "blog";
            var title = config["Blog:Title"] is { Length: > 0 } t ? t : "My blog";
            await tx.Exec("""
                INSERT INTO account (id, display_name, created_at) VALUES (@accountId, @name, @now);
                INSERT INTO blog (id, slug, title, created_at) VALUES (@blogId, @slug, @title, @now);
                INSERT INTO blog_member (blog_id, account_id, role, created_at) VALUES (@blogId, @accountId, 'owner', @now);
                """, new { accountId, blogId, slug, title, name = DisplayNameOf(identity), now });
            await tx.Event(new EventRow(null, "account", accountId, "account_created", Actors.Account(accountId), new { via = "setup" }), now);
            await tx.Event(new EventRow(blogId, "blog", blogId, "blog_created", Actors.Account(accountId), new { slug, title }), now);
        }
        else if (burned is not null)
        {
            // A new setup token re-opened setup: whoever had a session before it may be who lost the account.
            await tx.Exec("DELETE FROM account_session WHERE account_id = @accountId", new { accountId });
        }

        if (await BindAsync(tx, accountId, identity, now) is not { } bound || bound != accountId)
            throw new InvalidOperationException("The identity is bound to another account.");

        await tx.Exec("""
            INSERT INTO instance_setup (singleton, burned_token_hash, burned_at) VALUES (true, @tokenHash, @now)
            ON CONFLICT (singleton) DO UPDATE SET burned_token_hash = EXCLUDED.burned_token_hash, burned_at = EXCLUDED.burned_at
            """, new { tokenHash, now });
        await tx.Event(new EventRow(null, "account", accountId, burned is null ? "setup_completed" : "setup_reopened_and_completed",
            Actors.Account(accountId), new { provider = identity.Provider }), now);
        return accountId;
    }, ct);

    /// <summary>
    /// The account bound to this provider account (its hints refreshed), or null: not ours, or on no blog any more (someone
    /// the owner removed signs in to nothing; docs/specs/members.md).
    /// </summary>
    public Task<Guid?> SignInAsync(ProviderIdentity identity, CancellationToken ct) => db.WriteAsync(async tx =>
        await tx.One<Guid?>("""
            UPDATE account_identity SET last_used_at = @now, display_name = coalesce(@name, display_name)
            WHERE provider = @provider AND provider_account_id = @id
              AND EXISTS (SELECT 1 FROM blog_member m WHERE m.account_id = account_identity.account_id)
            RETURNING account_id
            """, new { now = clock.GetUtcNow(), name = identity.DisplayName, provider = identity.Provider, id = identity.AccountId }), ct);

    /// <summary>Binds another provider account to a signed-in account. False when it already belongs to someone else.</summary>
    /// <summary>The blog's owner, once setup has made one.</summary>
    public Task<Guid?> OwnerIdAsync(CancellationToken ct) => db.ReadAsync(tx =>
        tx.One<Guid?>("SELECT account_id FROM blog_member WHERE role = 'owner' ORDER BY created_at LIMIT 1"), ct);

    public Task<bool> BindAnotherAsync(Guid accountId, ProviderIdentity identity, CancellationToken ct) => db.WriteAsync(async tx =>
        await BindAsync(tx, accountId, identity, clock.GetUtcNow()) == accountId, ct);

    public Task<IReadOnlyList<Identity>> IdentitiesAsync(Guid accountId, CancellationToken ct) => db.ReadAsync<IReadOnlyList<Identity>>(async tx =>
        [.. await tx.Query<Identity>("""
            SELECT provider, provider_account_id, display_name, created_at, last_used_at
            FROM account_identity WHERE account_id = @accountId ORDER BY created_at
            """, new { accountId })], ct);

    public enum UnbindResult { Unbound, NotFound, LastOne }

    /// <summary>Unbinds one of the account's identities, ending the sessions it began. The last one stays: it's the way in.</summary>
    public Task<UnbindResult> UnbindAsync(Guid accountId, string provider, string providerAccountId, CancellationToken ct) => db.WriteAsync(async tx =>
    {
        var count = await tx.One<long>("SELECT count(*) FROM account_identity WHERE account_id = @accountId", new { accountId });
        var mine = await tx.One<bool>("""
            SELECT EXISTS (SELECT 1 FROM account_identity
                           WHERE account_id = @accountId AND provider = @provider AND provider_account_id = @providerAccountId)
            """, new { accountId, provider, providerAccountId });
        if (!mine) return UnbindResult.NotFound;
        if (count <= 1) return UnbindResult.LastOne;
        await tx.Exec("DELETE FROM account_identity WHERE provider = @provider AND provider_account_id = @providerAccountId",
            new { provider, providerAccountId });
        await tx.Event(new EventRow(null, "account", accountId, "identity_unbound", Actors.Account(accountId),
            new { provider }), clock.GetUtcNow());
        return UnbindResult.Unbound;
    }, ct);

    public Task<Me?> MeAsync(Guid accountId, CancellationToken ct) => db.ReadAsync<Me?>(async tx =>
    {
        var name = await tx.One<string>("SELECT display_name FROM account WHERE id = @accountId", new { accountId });
        if (name is null) return null;
        var blogs = await tx.Query<BlogMembership>("""
            SELECT b.id, b.slug, b.title, m.role, au.slug AS author_slug, au.profile_url AS author_profile_url FROM blog_member m JOIN blog b ON b.id = m.blog_id
            LEFT JOIN author au ON au.blog_id = m.blog_id AND au.account_id = m.account_id
            WHERE m.account_id = @accountId ORDER BY m.created_at
            """, new { accountId });
        return new Me(accountId, name, [.. blogs]);
    }, ct);

    /// <summary>Binds (or refreshes) the identity; returns the account it belongs to, which may be someone else's.</summary>
    private static async Task<Guid?> BindAsync(Tx tx, Guid accountId, ProviderIdentity identity, DateTimeOffset now)
    {
        var existing = await tx.One<Guid?>("""
            SELECT account_id FROM account_identity WHERE provider = @Provider AND provider_account_id = @AccountId
            """, identity);
        if (existing is { } owner) return owner;
        await tx.Exec("""
            INSERT INTO account_identity (provider, provider_account_id, account_id, display_name, created_at, last_used_at)
            VALUES (@provider, @id, @accountId, @name, @now, @now)
            """, new { provider = identity.Provider, id = identity.AccountId, accountId, name = identity.DisplayName, now });
        await tx.Event(new EventRow(null, "account", accountId, "identity_bound", Actors.Account(accountId),
            new { provider = identity.Provider }), now);
        return accountId;
    }

    private static string DisplayNameOf(ProviderIdentity identity) =>
        identity.DisplayName is { Length: > 0 } n ? n[..Math.Min(n.Length, 100)] : "Owner";
}
