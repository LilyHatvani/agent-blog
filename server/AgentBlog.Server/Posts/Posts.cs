using System.Text;
using System.Text.RegularExpressions;
using AgentBlog.Connections;
using AgentBlog.Database;

namespace AgentBlog.Posts;

/// <summary>Who is acting on a blog: its owner (no level, may do anything) or an agent connection at its level.</summary>
public sealed record Actor(Guid BlogId, string Value, string? Level, Guid? ConnectionId, Guid? AccountId)
{
    public bool IsOwner => Level is null;

    public static Actor Owner(Guid blogId, Guid accountId) => new(blogId, Actors.Account(accountId), null, null, accountId);

    public static Actor Agent(ConnectionStore.Caller c) => new(c.BlogId, Actors.Connection(c.ConnectionId), c.Level, c.ConnectionId, null);
}

public sealed record PostView(Guid Id, string Slug, string Status, Guid RevisionId, int RevisionNumber, string Title, string Excerpt,
    string? BodyMd, DateTime? PublishAt, DateTime? PublishedAt, DateTime UpdatedAt);

public sealed record RevisionView(Guid Id, int Number, string Title, string Actor, DateTime CreatedAt);

public sealed record RequestView(Guid Id, Guid BlogId, Guid PostId, string PostTitle, string Action, Guid PinnedRevisionId,
    string State, string ConnectionLabel, DateTime CreatedAt, DateTime? DecidedAt);

/// <summary>What a call came to: done, waiting for the owner (an approval request), or refused with a reason.</summary>
public sealed record PostOutcome(string Kind, PostView? Post = null, RequestView? Request = null, string? Code = null,
    string? Message = null, Guid? CurrentRevisionId = null)
{
    public const string Done = "done";
    public const string Waiting = "waiting_for_owner";
    public const string Refused = "refused";

    public static PostOutcome Ok(PostView post) => new(Done, post);
    public static PostOutcome Fail(string code, string message, Guid? current = null) => new(Refused, Code: code, Message: message, CurrentRevisionId: current);
}

/// <summary>
/// The post rules (docs/specs/posts.md), the one place they live: the MCP tools and the owner's API both call here
/// ("one contract"). Every save is a revision naming its actor; a write names the revision it read; anything above the
/// actor's level becomes an approval request pinned to that revision; nothing is deleted, trash is a bin.
/// </summary>
public sealed partial class PostService(Db db, TimeProvider clock)
{
    public const int MaxTitle = 300;
    public const int MaxBody = 500_000;
    public const int MaxExcerpt = 1000;

    /// <summary>The lowest level that runs an action at once (decision 0001). Below it, the action waits for the owner.</summary>
    public static string LevelFor(string action, string status) => action switch
    {
        "create" or "submit" => Levels.Propose,
        "edit" => status is "live" or "scheduled" ? Levels.Publish : Levels.Propose,
        "publish" or "schedule" or "unpublish" => Levels.Publish,
        "trash" or "restore" => Levels.Full,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static bool Allows(Actor actor, string needed) => actor.IsOwner || Levels.Rank(actor.Level!) >= Levels.Rank(needed);

    private static string Why(Actor actor, string needed) =>
        $"This needs {needed}; your connection is {actor.Level}. The owner decides.";

    // ------------------------------------------------------------------------------------------------ reads

    public Task<IReadOnlyList<PostView>> ListAsync(Guid blogId, string? status, string? text, int limit, int offset, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<PostView>>(async tx => [.. await tx.Query<PostView>($"""
            {SelectView(withBody: false)}
            WHERE p.blog_id = @blogId
              AND (@status::text IS NULL OR p.status = @status)
              AND (@text::text IS NULL OR r.title ILIKE '%' || @text || '%' OR r.body_md ILIKE '%' || @text || '%')
            ORDER BY p.updated_at DESC, p.id
            LIMIT @limit OFFSET @offset
            """, new { blogId, status, text, limit = Math.Clamp(limit, 1, 100), offset = Math.Max(offset, 0) })], ct);

    /// <summary>One post of this blog, by id or slug, with its body. Another blog's post reads as not found.</summary>
    public Task<PostView?> ReadAsync(Guid blogId, string idOrSlug, CancellationToken ct) => db.ReadAsync(tx => FindAsync(tx, blogId, idOrSlug, withBody: true), ct);

    public Task<IReadOnlyList<RevisionView>?> RevisionsAsync(Guid blogId, string idOrSlug, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<RevisionView>?>(async tx =>
        {
            if (await FindAsync(tx, blogId, idOrSlug, withBody: false) is not { } post) return null;
            return [.. await tx.Query<RevisionView>("""
                SELECT id, number, title, actor, created_at FROM post_revision
                WHERE blog_id = @blogId AND post_id = @id ORDER BY number DESC
                """, new { blogId, id = post.Id })];
        }, ct);

    // ----------------------------------------------------------------------------------------------- writes

    public Task<PostOutcome> CreateDraftAsync(Actor actor, string title, string bodyMd, string? excerpt, CancellationToken ct)
    {
        if (Invalid(title, bodyMd, excerpt) is { } bad) return Task.FromResult(bad);
        return db.WriteAsync(async tx =>
        {
            var now = clock.GetUtcNow();
            var postId = Guid.CreateVersion7();
            var revisionId = Guid.CreateVersion7();
            var slug = await UniqueSlugAsync(tx, actor.BlogId, Slugify(title));
            await tx.Exec("""
                INSERT INTO post (id, blog_id, slug, current_revision_id, created_at, updated_at)
                VALUES (@postId, @BlogId, @slug, @revisionId, @now, @now);
                INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, excerpt, actor, created_at)
                VALUES (@revisionId, @BlogId, @postId, 1, @title, @bodyMd, @excerpt, @Value, @now);
                """, new { postId, actor.BlogId, slug, revisionId, title, bodyMd, excerpt = excerpt ?? "", actor.Value, now });
            await tx.Event(new EventRow(actor.BlogId, "post", postId, "post_created", actor.Value, new { slug, revision = 1 }), now);
            return PostOutcome.Ok((await FindAsync(tx, actor.BlogId, postId.ToString(), withBody: true))!);
        }, ct);
    }

    /// <summary>A new revision. Any of title, body, excerpt; the others carry over. <paramref name="baseRevision"/> is required.</summary>
    public Task<PostOutcome> EditAsync(Actor actor, string idOrSlug, Guid baseRevision, string? title, string? bodyMd, string? excerpt, CancellationToken ct) =>
        db.WriteAsync(async tx =>
        {
            if (await LockAsync(tx, actor.BlogId, idOrSlug) is not { } post) return NotFound();
            if (post.RevisionId != baseRevision) return Stale(post);
            if (post.Status == "trash") return PostOutcome.Fail("invalid_state", "The post is in the trash. Restore it first.");
            var current = await tx.One<RevisionContent>("SELECT title, body_md, excerpt FROM post_revision WHERE id = @RevisionId", post);
            var next = new RevisionContent(title ?? current!.Title, bodyMd ?? current!.BodyMd, excerpt ?? current!.Excerpt);
            if (Invalid(next.Title, next.BodyMd, next.Excerpt) is { } bad) return bad;

            var needed = LevelFor("edit", post.Status);
            if (!Allows(actor, needed))
                return await RequestAsync(tx, actor, post, "edit", new { title = next.Title, body_md = next.BodyMd, excerpt = next.Excerpt }, needed);
            await SaveRevisionAsync(tx, actor.BlogId, post.Id, next, actor.Value, clock.GetUtcNow(), "revision_saved");
            return PostOutcome.Ok((await FindAsync(tx, actor.BlogId, post.Id.ToString(), withBody: true))!);
        }, ct);

    /// <summary>
    /// A state change: submit, publish, schedule (with <paramref name="at"/>), unpublish, trash, restore. A
    /// <paramref name="baseRevision"/>, when given, must still be current: what is published is what was read.
    /// </summary>
    public Task<PostOutcome> TransitionAsync(Actor actor, string idOrSlug, string action, Guid? baseRevision, DateTimeOffset? at, CancellationToken ct) =>
        db.WriteAsync(async tx =>
        {
            if (await LockAsync(tx, actor.BlogId, idOrSlug) is not { } post) return NotFound();
            if (baseRevision is { } b && post.RevisionId != b) return Stale(post);
            if (CheckState(action, post.Status, at, clock.GetUtcNow()) is { } bad) return bad;

            var needed = LevelFor(action, post.Status);
            if (!Allows(actor, needed))
                return await RequestAsync(tx, actor, post, action, at is { } when ? new { at = when } : new { }, needed);
            await ApplyAsync(tx, actor.BlogId, post, action, at, actor.Value, clock.GetUtcNow());
            return PostOutcome.Ok((await FindAsync(tx, actor.BlogId, post.Id.ToString(), withBody: false))!);
        }, ct);

    /// <summary>The owner puts an old revision back: a new revision with its content, so nothing in history is lost.</summary>
    public Task<PostOutcome> RestoreRevisionAsync(Actor owner, string idOrSlug, Guid revisionId, CancellationToken ct) =>
        db.WriteAsync(async tx =>
        {
            if (!owner.IsOwner) return PostOutcome.Fail("forbidden", "Only the owner restores revisions.");
            if (await LockAsync(tx, owner.BlogId, idOrSlug) is not { } post) return NotFound();
            var old = await tx.One<RevisionContent>("""
                SELECT title, body_md, excerpt FROM post_revision WHERE blog_id = @BlogId AND post_id = @postId AND id = @revisionId
                """, new { owner.BlogId, postId = post.Id, revisionId });
            if (old is null) return PostOutcome.Fail("not_found", "That revision isn't one of this post's.");
            await SaveRevisionAsync(tx, owner.BlogId, post.Id, old, owner.Value, clock.GetUtcNow(), "revision_restored", new { from = revisionId });
            return PostOutcome.Ok((await FindAsync(tx, owner.BlogId, post.Id.ToString(), withBody: true))!);
        }, ct);

    // ------------------------------------------------------------------------------------- approval requests

    /// <summary>An agent's own request, for get_request. Another connection's reads as not found.</summary>
    public Task<RequestView?> RequestForAgentAsync(Guid connectionId, Guid requestId, CancellationToken ct) =>
        db.ReadAsync(tx => tx.One<RequestView>($"{SelectRequest} WHERE q.id = @requestId AND q.connection_id = @connectionId",
            new { requestId, connectionId }), ct);

    public Task<IReadOnlyList<RequestView>> WaitingForAsync(Guid accountId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<RequestView>>(async tx => [.. await tx.Query<RequestView>($"""
            {SelectRequest}
            JOIN blog_member m ON m.blog_id = q.blog_id AND m.account_id = @accountId AND m.role = 'owner'
            WHERE q.state = 'waiting' ORDER BY q.created_at
            """, new { accountId })], ct);

    /// <summary>
    /// The owner's decision. Approving applies exactly the pinned change, and only if the post is still at the revision
    /// the request was made against; otherwise the request turns stale and nothing applies.
    /// </summary>
    public Task<PostOutcome> DecideAsync(Guid accountId, Guid requestId, bool approve, CancellationToken ct) =>
        db.WriteAsync(async tx =>
        {
            var q = await tx.One<RequestRow>("""
                SELECT q.id, q.blog_id, q.connection_id, q.action, q.post_id, q.pinned_revision_id, q.payload::text AS payload, q.state
                FROM approval_request q
                JOIN blog_member m ON m.blog_id = q.blog_id AND m.account_id = @accountId AND m.role = 'owner'
                WHERE q.id = @requestId FOR UPDATE OF q
                """, new { accountId, requestId });
            if (q is null) return PostOutcome.Fail("not_found", "No such request on your blogs.");
            if (q.State != "waiting") return PostOutcome.Fail("invalid_state", $"This request is already {q.State}.");
            var now = clock.GetUtcNow();
            var owner = Actors.Account(accountId);

            if (!approve)
            {
                await SetStateAsync(tx, q, "declined", accountId, now);
                await tx.Event(new EventRow(q.BlogId, "approval_request", q.Id, "approval_declined", owner, new { q.Action }), now);
                return new PostOutcome(PostOutcome.Done, Request: await RequestViewAsync(tx, q.Id));
            }

            var post = (await LockAsync(tx, q.BlogId, q.PostId.ToString()))!;
            var payload = System.Text.Json.JsonDocument.Parse(q.Payload).RootElement;
            DateTimeOffset? at = payload.TryGetProperty("at", out var a) ? a.GetDateTimeOffset() : null;
            if (post.RevisionId != q.PinnedRevisionId || (q.Action != "edit" && CheckState(q.Action, post.Status, at, now) is not null))
            {
                await SetStateAsync(tx, q, "stale", accountId, now);
                await tx.Event(new EventRow(q.BlogId, "approval_request", q.Id, "approval_stale", owner, new { q.Action }), now);
                return PostOutcome.Fail("stale", "The post changed since the agent asked, so this request no longer applies. Nothing was changed.",
                    post.RevisionId);
            }

            if (q.Action == "edit")
            {
                // The words are the agent's: the revision names the connection; the event names who approved it.
                var content = new RevisionContent(payload.GetProperty("title").GetString()!, payload.GetProperty("body_md").GetString()!,
                    payload.GetProperty("excerpt").GetString()!);
                await SaveRevisionAsync(tx, q.BlogId, post.Id, content, Actors.Connection(q.ConnectionId), now, "revision_saved",
                    new { approved_by = owner });
            }
            else
            {
                await ApplyAsync(tx, q.BlogId, post, q.Action, at, owner, now, requestedBy: Actors.Connection(q.ConnectionId));
            }
            await SetStateAsync(tx, q, "approved", accountId, now);
            await tx.Event(new EventRow(q.BlogId, "approval_request", q.Id, "approval_approved", owner, new { q.Action }), now);
            return new PostOutcome(PostOutcome.Done, await FindAsync(tx, q.BlogId, post.Id.ToString(), withBody: false), await RequestViewAsync(tx, q.Id));
        }, ct);

    // ---------------------------------------------------------------------------------------------- the core

    private static PostOutcome? CheckState(string action, string status, DateTimeOffset? at, DateTimeOffset now) => action switch
    {
        "submit" when status != "draft" => PostOutcome.Fail("invalid_state", $"Only a draft can be submitted for review; this one is {status}."),
        "publish" when status is not ("draft" or "review") => PostOutcome.Fail("invalid_state", $"Only a draft or a post in review can be published; this one is {status}."),
        "schedule" when status is not ("draft" or "review") => PostOutcome.Fail("invalid_state", $"Only a draft or a post in review can be scheduled; this one is {status}."),
        "schedule" when at is null || at <= now => PostOutcome.Fail("invalid_argument", "A schedule needs a time in the future (ISO 8601, with a time zone)."),
        "unpublish" when status is not ("live" or "scheduled") => PostOutcome.Fail("invalid_state", $"Only a live or scheduled post can be unpublished; this one is {status}."),
        "trash" when status == "trash" => PostOutcome.Fail("invalid_state", "The post is already in the trash."),
        "restore" when status != "trash" => PostOutcome.Fail("invalid_state", "Only a post in the trash can be restored."),
        _ => null,
    };

    private static async Task ApplyAsync(Tx tx, Guid blogId, PostRow post, string action, DateTimeOffset? at, string actor, DateTimeOffset now,
        string? requestedBy = null)
    {
        var p = new { post.Id, now, at, actor };
        switch (action)
        {
            case "submit":
                await tx.Exec("UPDATE post SET status = 'review', updated_at = @now WHERE id = @Id", p);
                break;
            case "publish":
                await tx.Exec("""
                    UPDATE post SET status = 'live', published_at = coalesce(published_at, @now), publish_at = NULL, scheduled_by = NULL,
                                    updated_at = @now
                    WHERE id = @Id
                    """, p);
                break;
            case "schedule":
                // Whoever set the schedule is judged again when it fires (decision 0001): an approved request is the owner's.
                await tx.Exec("UPDATE post SET status = 'scheduled', publish_at = @at, scheduled_by = @actor, updated_at = @now WHERE id = @Id", p);
                break;
            case "unpublish":
                await tx.Exec("UPDATE post SET status = 'draft', publish_at = NULL, scheduled_by = NULL, updated_at = @now WHERE id = @Id", p);
                break;
            case "trash":
                // A schedule doesn't survive the trash: restoring it comes back as a draft.
                await tx.Exec("""
                    UPDATE post SET status_before_trash = CASE WHEN status = 'scheduled' THEN 'draft' ELSE status END,
                                    status = 'trash', trashed_at = @now, trashed_by = @actor, publish_at = NULL, scheduled_by = NULL,
                                    updated_at = @now
                    WHERE id = @Id
                    """, p);
                break;
            case "restore":
                await tx.Exec("""
                    UPDATE post SET status = status_before_trash, status_before_trash = NULL, trashed_at = NULL, trashed_by = NULL,
                                    updated_at = @now
                    WHERE id = @Id
                    """, p);
                break;
        }
        await tx.Event(new EventRow(blogId, "post", post.Id, action switch
        {
            "submit" => "post_submitted",
            "publish" => "post_published",
            "schedule" => "post_scheduled",
            "unpublish" => "post_unpublished",
            "trash" => "post_trashed",
            _ => "post_restored",
        }, actor, new { revision = post.RevisionNumber, at, requested_by = requestedBy }), now);
    }

    private static async Task SaveRevisionAsync(Tx tx, Guid blogId, Guid postId, RevisionContent c, string actor, DateTimeOffset now,
        string kind, object? extra = null)
    {
        var revisionId = Guid.CreateVersion7();
        var number = await tx.One<int>("""
            INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, excerpt, actor, created_at)
            SELECT @revisionId, @blogId, @postId, coalesce(max(number), 0) + 1, @Title, @BodyMd, @Excerpt, @actor, @now
            FROM post_revision WHERE post_id = @postId
            RETURNING number
            """, new { revisionId, blogId, postId, c.Title, c.BodyMd, c.Excerpt, actor, now });
        await tx.Exec("UPDATE post SET current_revision_id = @revisionId, updated_at = @now WHERE id = @postId", new { revisionId, postId, now });
        await tx.Event(new EventRow(blogId, "post", postId, kind, actor, new { revision = number, extra }), now);
    }

    private async Task<PostOutcome> RequestAsync(Tx tx, Actor actor, PostRow post, string action, object payload, string needed)
    {
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        await tx.Exec("""
            INSERT INTO approval_request (id, blog_id, connection_id, action, post_id, pinned_revision_id, payload, created_at)
            VALUES (@id, @BlogId, @connectionId, @action, @postId, @pinned, @payload::jsonb, @now)
            """, new
        {
            id, actor.BlogId, connectionId = actor.ConnectionId!.Value, action, postId = post.Id, pinned = post.RevisionId,
            payload = System.Text.Json.JsonSerializer.Serialize(payload, Json.Options), now,
        });
        await tx.Event(new EventRow(actor.BlogId, "approval_request", id, "approval_requested", actor.Value,
            new { action, post = post.Id, revision = post.RevisionNumber }), now);
        return new PostOutcome(PostOutcome.Waiting, Request: await RequestViewAsync(tx, id), Message: Why(actor, needed));
    }

    private static Task SetStateAsync(Tx tx, RequestRow q, string state, Guid accountId, DateTimeOffset now) =>
        tx.Exec("UPDATE approval_request SET state = @state, decided_at = @now, decided_by = @accountId WHERE id = @Id",
            new { state, now, accountId, q.Id });

    private static Task<RequestView?> RequestViewAsync(Tx tx, Guid id) => tx.One<RequestView>($"{SelectRequest} WHERE q.id = @id", new { id });

    private const string SelectRequest = """
        SELECT q.id, q.blog_id, q.post_id, r.title AS post_title, q.action, q.pinned_revision_id, q.state, c.label AS connection_label,
               q.created_at, q.decided_at
        FROM approval_request q
        JOIN post p ON p.id = q.post_id
        JOIN post_revision r ON r.id = p.current_revision_id
        JOIN connection c ON c.id = q.connection_id
        """;

    private static string SelectView(bool withBody) => $"""
        SELECT p.id, p.slug, p.status, r.id AS revision_id, r.number AS revision_number, r.title, r.excerpt,
               {(withBody ? "r.body_md" : "NULL::text AS body_md")}, p.publish_at, p.published_at, p.updated_at
        FROM post p JOIN post_revision r ON r.id = p.current_revision_id
        """;

    private static Task<PostView?> FindAsync(Tx tx, Guid blogId, string idOrSlug, bool withBody) => tx.One<PostView>($"""
        {SelectView(withBody)}
        WHERE p.blog_id = @blogId AND {(Guid.TryParse(idOrSlug, out _) ? "p.id = @id::uuid" : "p.slug = @id")}
        """, new { blogId, id = idOrSlug });

    private static Task<PostRow?> LockAsync(Tx tx, Guid blogId, string idOrSlug) => tx.One<PostRow>($"""
        SELECT p.id, p.status, p.current_revision_id AS revision_id, r.number AS revision_number
        FROM post p JOIN post_revision r ON r.id = p.current_revision_id
        WHERE p.blog_id = @blogId AND {(Guid.TryParse(idOrSlug, out _) ? "p.id = @id::uuid" : "p.slug = @id")}
        FOR UPDATE OF p
        """, new { blogId, id = idOrSlug });

    private static PostOutcome NotFound() => PostOutcome.Fail("not_found", "No such post on this blog.");

    private static PostOutcome Stale(PostRow post) => PostOutcome.Fail("stale_revision",
        $"The post moved on to revision {post.RevisionNumber} since you read it. Read it again and redo your change on top.", post.RevisionId);

    private static PostOutcome? Invalid(string title, string body, string? excerpt) =>
        string.IsNullOrWhiteSpace(title) || title.Length > MaxTitle ? PostOutcome.Fail("invalid_argument", $"title is 1–{MaxTitle} characters.")
        : body.Length > MaxBody ? PostOutcome.Fail("invalid_argument", $"body_md is at most {MaxBody} characters.")
        : excerpt?.Length > MaxExcerpt ? PostOutcome.Fail("invalid_argument", $"excerpt is at most {MaxExcerpt} characters.")
        : null;

    internal static string Slugify(string title)
    {
        var plain = new StringBuilder();
        foreach (var ch in title.Normalize(NormalizationForm.FormD))
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                plain.Append(char.ToLowerInvariant(ch));
        var slug = NonSlug().Replace(plain.ToString(), "-").Trim('-');
        if (slug.Length > 80) slug = slug[..80].TrimEnd('-');
        return slug.Length == 0 ? "post" : slug;
    }

    private static async Task<string> UniqueSlugAsync(Tx tx, Guid blogId, string slug)
    {
        var taken = (await tx.Query<string>("SELECT slug FROM post WHERE blog_id = @blogId AND (slug = @slug OR slug LIKE @slug || '-%')",
            new { blogId, slug })).ToHashSet();
        if (!taken.Contains(slug)) return slug;
        for (var n = 2; ; n++)
            if (!taken.Contains($"{slug}-{n}")) return $"{slug}-{n}";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    private sealed record RevisionContent(string Title, string BodyMd, string Excerpt);

    internal sealed record PostRow(Guid Id, string Status, Guid RevisionId, int RevisionNumber);

    private sealed record RequestRow(Guid Id, Guid BlogId, Guid ConnectionId, string Action, Guid PostId, Guid PinnedRevisionId,
        string Payload, string State);
}
