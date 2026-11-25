using AgentBlog.Database;

namespace AgentBlog.Import;

/// <summary>
/// "This is me" (docs/specs/import.md § This is me): an author the import made a writer, who is really someone already on
/// the blog, is folded into that account. Revisions and events are append-only, so nothing they say is rewritten: an
/// <c>author_fold</c> row says the folded account is the other one, and every reader of a post's people resolves it
/// (<see cref="AccountActorsSql"/>). Only an account the import made here, that nobody has any way in to, is ever folded:
/// a person who has signed in, even once, keeps their own posts.
/// </summary>
public static class AuthorFold
{
    /// <summary>
    /// SQL for the actors that are <c>@personId</c>'s own account on the post's blog: their account, and every account
    /// folded into it. Takes <c>@personId</c>; <paramref name="blogColumn"/> is the blog the actor is read on.
    /// </summary>
    public static string AccountActorsSql(string blogColumn) => $"""
        (SELECT 'account:' || @personId::text
         UNION ALL SELECT 'account:' || f.account_id::text FROM author_fold f WHERE f.blog_id = {blogColumn} AND f.into_account_id = @personId)
        """;

    /// <summary>The folds on a blog: each folded account to the one it reads as.</summary>
    public static async Task<Dictionary<Guid, Guid>> OnBlogAsync(Tx tx, Guid blogId) =>
        (await tx.Query<(Guid From, Guid Into)>("SELECT account_id, into_account_id FROM author_fold WHERE blog_id = @blogId", new { blogId }))
            .ToDictionary(f => f.From, f => f.Into);

    /// <summary>
    /// Folds <paramref name="from"/> into <paramref name="into"/> on the blog, inside the caller's transaction (which holds
    /// the event lock: never call it inside another <see cref="Db.WriteAsync{T}"/>). Null when folded; otherwise why not.
    /// </summary>
    public static async Task<string?> FoldAsync(Tx tx, Guid blogId, Guid from, Guid into, string actor, DateTimeOffset now)
    {
        if (from == into) return "That is the same account.";
        // The account row is locked first, as accepting a claim locks it: a claim and a fold of the same author can't
        // both go through.
        if (await tx.One<Guid?>("SELECT id FROM account WHERE id = @from FOR UPDATE", new { from }) is null) return NotFoldable;
        var foldable = await tx.One<bool>("""
            SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @blogId AND account_id = @from AND role <> 'owner')
               AND NOT EXISTS (SELECT 1 FROM blog_member WHERE account_id = @from AND blog_id <> @blogId)
               AND EXISTS (SELECT 1 FROM import_author WHERE blog_id = @blogId AND account_id = @from)
               AND NOT EXISTS (SELECT 1 FROM account_identity WHERE account_id = @from)
               AND NOT EXISTS (SELECT 1 FROM connection WHERE created_by = @from)
               AND NOT EXISTS (SELECT 1 FROM author_fold WHERE account_id = @from)
               -- A way in leaves a mark the history keeps, export and restore included (identities don't travel, events
               -- do): an invitation accepted, a claim, anything saved or done as themselves. Only what the import does
               -- in their name doesn't count.
               AND NOT EXISTS (SELECT 1 FROM event WHERE blog_id = @blogId AND actor = 'account:' || @from::text
                                 AND kind <> ALL (@importKinds))
            """, new { blogId, from, importKinds = ImportKinds });
        if (!foldable) return NotFoldable;
        if (!await tx.One<bool>("SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @blogId AND account_id = @into)", new { blogId, into }))
            return "The account to fold into isn't on this blog.";

        await tx.Exec("INSERT INTO author_fold (blog_id, account_id, into_account_id, created_at) VALUES (@blogId, @from, @into, @now)",
            new { blogId, from, into, now });
        // Folds never chain: whoever was folded into this author reads as the account it is folded into now.
        await tx.Exec("UPDATE author_fold SET into_account_id = @into WHERE blog_id = @blogId AND into_account_id = @from", new { blogId, from, into });
        // Later runs land this author's posts on the account straight away.
        await tx.Exec("UPDATE import_author SET account_id = @into WHERE blog_id = @blogId AND account_id = @from", new { blogId, from, into });
        await tx.Exec("""
            UPDATE invitation SET withdrawn_at = @now
            WHERE blog_id = @blogId AND account_id = @from AND used_at IS NULL AND withdrawn_at IS NULL
            """, new { blogId, from, now });
        await tx.Exec("DELETE FROM blog_member WHERE blog_id = @blogId AND account_id = @from", new { blogId, from });

        // Their author address moves to the account's page; a row that moved the account's own address to theirs would now
        // loop, so it goes; any other row that led to theirs leads to the account's.
        var slugs = await tx.Query<(Guid Account, string Slug)>(
            "SELECT account_id, slug FROM author WHERE blog_id = @blogId AND account_id IN (@from, @into)", new { blogId, from, into });
        var fromSlug = slugs.FirstOrDefault(s => s.Account == from).Slug;
        var intoSlug = slugs.FirstOrDefault(s => s.Account == into).Slug;
        if (fromSlug is { Length: > 0 } && intoSlug is { Length: > 0 })
        {
            string fromPath = "/author/" + fromSlug, intoPath = "/author/" + intoSlug;
            await tx.Exec("DELETE FROM old_address WHERE blog_id = @blogId AND path = @intoPath AND target = @fromPath", new { blogId, intoPath, fromPath });
            await tx.Exec("UPDATE old_address SET target = @intoPath WHERE blog_id = @blogId AND target = @fromPath AND path <> @intoPath",
                new { blogId, fromPath, intoPath });
            await tx.Exec("""
                INSERT INTO old_address (blog_id, path, target) VALUES (@blogId, @fromPath, @intoPath)
                ON CONFLICT (blog_id, path) DO UPDATE SET target = excluded.target
                """, new { blogId, fromPath, intoPath });
        }
        await tx.Event(new EventRow(blogId, "account", from, "author_folded", actor, new { into }), now);
        return null;
    }

    /// <summary>The events the import writes in an imported author's name (their posts, updates, terms and pictures).</summary>
    internal static readonly string[] ImportKinds = ["post_imported", "post_import_updated", "term_created", "media_upload_started", "media_added"];

    /// <summary>Whether <paramref name="account"/> stands for exactly one source author on the blog: this one.</summary>
    public static Task<bool> OnlyForAsync(Tx tx, Guid blogId, Guid account, string source, string key) => tx.One<bool>("""
        SELECT NOT EXISTS (SELECT 1 FROM import_author WHERE blog_id = @blogId AND account_id = @account
                           AND (source <> @source OR source_key <> @key))
        """, new { blogId, account, source, key });

    public const string NotFoldable =
        "Only an author the import brought in, whom nobody has signed in as, can be folded; anyone who can sign in keeps their own posts.";
}
