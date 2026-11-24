using System.Text;
using System.Text.RegularExpressions;
using AgentBlog.Auth;
using AgentBlog.Connections;
using AgentBlog.Database;

namespace AgentBlog.Posts;

/// <summary>Who is acting on a blog: its owner (no level, may do anything) or an agent connection at its level.</summary>
public sealed record Actor(Guid BlogId, string Value, string? Level, Guid? ConnectionId, Guid? AccountId, string? Role = null)
{
    /// <summary>The blog's owner (a person, not an agent).</summary>
    public bool IsOwner => ConnectionId is null && Role == Roles.Owner;

    /// <summary>A person on the blog (owner, editor or writer), not an agent.</summary>
    public bool IsPerson => ConnectionId is null;

    /// <summary>The owner or an editor: edits any post and publishes (docs/specs/members.md).</summary>
    public bool Edits => IsPerson && Role is Roles.Owner or Roles.Editor;

    public static Actor Owner(Guid blogId, Guid accountId) => new(blogId, Actors.Account(accountId), null, null, accountId, Roles.Owner);

    /// <summary>A person on the blog in <paramref name="role"/>.</summary>
    public static Actor Person(Guid blogId, Guid accountId, string role) => new(blogId, Actors.Account(accountId), null, null, accountId, role);

    /// <summary>An agent: its working level, and the person whose agent it is, whose reach it never exceeds.</summary>
    public static Actor Agent(ConnectionStore.Caller c) => new(c.BlogId, Actors.Connection(c.ConnectionId), c.Level, c.ConnectionId, c.PersonId, c.Role);
}

public sealed record PostView(Guid Id, string Slug, string Status, Guid RevisionId, int RevisionNumber, string Title, string Excerpt,
    string? BodyMd, DateTime? PublishAt, DateTime? PublishedAt, DateTime UpdatedAt, string TermsJson, string SuggestedJson)
{
    public List<TermRef> Terms => Taxonomy.Parse(TermsJson);
    public List<TermSuggestion> Suggested => Taxonomy.ParseSuggested(SuggestedJson);
}

/// <summary>The owner's own changes on top of what they approve (Approve with edits). A field left null stays.</summary>
public sealed record OwnerEdits(string? Title, string? BodyMd, string? Excerpt)
{
    public bool IsEmpty => Title is null && BodyMd is null && Excerpt is null;
}

public sealed record RevisionView(Guid Id, int Number, string Title, string Actor, string ActorLabel, DateTime CreatedAt);

public sealed record RevisionContentView(Guid Id, int Number, string Title, string Excerpt, string BodyMd, string Actor, string ActorLabel, DateTime CreatedAt);

public sealed record RequestView(Guid Id, Guid BlogId, Guid PostId, string PostTitle, string Action, Guid PinnedRevisionId,
    string State, string ConnectionLabel, DateTime CreatedAt, DateTime? DecidedAt, string Payload, Guid CurrentRevisionId,
    string CurrentBodyMd, string CurrentExcerpt, string CurrentTermsJson, string ProposedTermsJson, string CurrentStatus, string? DecisionNote,
    bool DecidedByOwner, string? DecidedByName, string AgentPerson, bool AgentPersonIsOwner);

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

    /// <summary>
    /// Whether <paramref name="actor"/> runs an action needing <paramref name="needed"/> at once: an agent by its level
    /// (below it, a request to the owner); a person by their role (docs/specs/members.md): the owner anything, an editor
    /// up to Publish, a writer Propose.
    /// </summary>
    private static bool Allows(Actor actor, string needed) => actor.IsPerson
        ? actor.Role switch
        {
            Roles.Owner => true,
            Roles.Editor => Levels.Rank(needed) <= Levels.Rank(Levels.Publish),
            Roles.Writer => needed == Levels.Propose,
            _ => false,
        }
        : Levels.Rank(actor.Level!) >= Levels.Rank(needed);

    private static string Why(Actor actor, string needed) =>
        $"This needs {needed}; your connection is {actor.Level}. The owner decides.";

    /// <summary>Why a person may not do this, in words; a person never makes an approval request (agents do).</summary>
    private static PostOutcome Refused(Actor actor, string action) => PostOutcome.Fail("forbidden", actor.Role switch
    {
        Roles.Writer => action switch
        {
            "edit" => "A writer edits their own posts while they are drafts or in review; once it's live or scheduled, an editor or the owner changes it.",
            "trash" or "restore" => "Only the owner trashes and restores posts.",
            _ => "A writer drafts and submits for review; an editor or the owner publishes.",
        },
        Roles.Editor => "Only the owner trashes and restores posts.",
        _ => "This isn't yours to do on this blog.",
    });

    /// <summary>
    /// Whether a post is <paramref name="personId"/>'s own: its first revision was saved by them, or by an agent of theirs.
    /// The SQL takes <c>@postId</c> and <c>@personId</c>.
    /// </summary>
    internal const string IsOwnPostSql = """
        SELECT EXISTS (SELECT 1 FROM post_revision r WHERE r.post_id = @postId AND r.number = 1
            AND (r.actor = 'account:' || @personId::text
                 OR r.actor IN (SELECT 'connection:' || c.id::text FROM connection c WHERE c.blog_id = r.blog_id AND c.created_by = @personId)))
        """;

    /// <summary>
    /// A writer, and a writer's agent, act only on the writer's own posts (docs/specs/members.md); everyone else as their
    /// role or level allows.
    /// </summary>
    private static async Task<PostOutcome?> WriterOwnsAsync(Tx tx, Actor actor, Guid postId) =>
        actor.Role == Roles.Writer && actor.AccountId is { } personId && !await tx.One<bool>(IsOwnPostSql, new { postId, personId })
            ? PostOutcome.Fail("forbidden", actor.IsPerson
                ? "A writer changes only their own posts; this one is someone else's."
                : "You're a writer's agent: you change only your writer's posts (theirs or ones you started), and this one is someone else's.")
            : null;

    // ------------------------------------------------------------------------------------------------ reads

    public Task<IReadOnlyList<PostView>> ListAsync(Guid blogId, string? status, string? text, int limit, int offset, CancellationToken ct,
        string? category = null, string? tag = null) =>
        db.ReadAsync<IReadOnlyList<PostView>>(async tx => [.. await tx.Query<PostView>($"""
            {SelectView(withBody: false)}
            WHERE p.blog_id = @blogId
              AND (@status::text IS NULL OR p.status = @status)
              AND (@text::text IS NULL OR r.title ILIKE '%' || @text || '%' OR r.body_md ILIKE '%' || @text || '%')
              AND (@category::text IS NULL OR EXISTS (SELECT 1 FROM term t WHERE t.blog_id = p.blog_id AND t.kind = 'category'
                                                      AND t.slug = @category AND r.term_ids @> ARRAY[t.id]))
              AND (@tag::text IS NULL OR EXISTS (SELECT 1 FROM term t WHERE t.blog_id = p.blog_id AND t.kind = 'tag'
                                                 AND t.slug = @tag AND r.term_ids @> ARRAY[t.id]))
            ORDER BY p.updated_at DESC, p.id
            LIMIT @limit OFFSET @offset
            """, new { blogId, status, text, category, tag, limit = Math.Clamp(limit, 1, 100), offset = Math.Max(offset, 0) })], ct);

    /// <summary>One post of this blog, by id or slug, with its body. Another blog's post reads as not found.</summary>
    public Task<PostView?> ReadAsync(Guid blogId, string idOrSlug, CancellationToken ct) => db.ReadAsync(tx => FindAsync(tx, blogId, idOrSlug, withBody: true), ct);

    public Task<IReadOnlyList<RevisionView>?> RevisionsAsync(Guid blogId, string idOrSlug, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<RevisionView>?>(async tx =>
        {
            if (await FindAsync(tx, blogId, idOrSlug, withBody: false) is not { } post) return null;
            return [.. await tx.Query<RevisionView>("""
                SELECT id, number, title, actor, actor_label(actor) AS actor_label, created_at FROM post_revision
                WHERE blog_id = @blogId AND post_id = @id ORDER BY number DESC
                """, new { blogId, id = post.Id })];
        }, ct);

    /// <summary>One revision's full content, for the owner's history view.</summary>
    public Task<RevisionContentView?> RevisionAsync(Guid blogId, Guid postId, Guid revisionId, CancellationToken ct) =>
        db.ReadAsync(tx => tx.One<RevisionContentView>("""
            SELECT id, number, title, excerpt, body_md, actor, actor_label(actor) AS actor_label, created_at FROM post_revision
            WHERE blog_id = @blogId AND post_id = @postId AND id = @revisionId
            """, new { blogId, postId, revisionId }), ct);

    // ----------------------------------------------------------------------------------------------- writes

    public Task<PostOutcome> CreateDraftAsync(Actor actor, string title, string bodyMd, string? excerpt, CancellationToken ct,
        IReadOnlyList<string>? categories = null, IReadOnlyList<string>? tags = null)
    {
        if (Invalid(title, bodyMd, excerpt) is { } bad) return Task.FromResult(bad);
        return db.WriteAsync(async tx =>
        {
            var now = clock.GetUtcNow();
            var terms = await TermsForAsync(tx, actor, [], "[]", categories, tags, now);
            if (terms.Error is { } termError) return termError;
            var postId = Guid.CreateVersion7();
            var revisionId = Guid.CreateVersion7();
            var slug = await UniqueSlugAsync(tx, actor.BlogId, Slugify(title));
            await tx.Exec("""
                INSERT INTO post (id, blog_id, slug, current_revision_id, created_at, updated_at)
                VALUES (@postId, @BlogId, @slug, @revisionId, @now, @now);
                INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, excerpt, term_ids, suggested_terms, actor, created_at)
                VALUES (@revisionId, @BlogId, @postId, 1, @title, @bodyMd, @excerpt, @termIds, @suggested::jsonb, @Value, @now);
                """, new { postId, actor.BlogId, slug, revisionId, title, bodyMd, excerpt = excerpt ?? "", termIds = terms.Ids,
                suggested = terms.Suggested, actor.Value, now });
            await tx.Event(new EventRow(actor.BlogId, "post", postId, "post_created", actor.Value, new { slug, revision = 1 }), now);
            return PostOutcome.Ok((await FindAsync(tx, actor.BlogId, postId.ToString(), withBody: true))!);
        }, ct);
    }

    /// <summary>How an import went for one post: brought in, already here (by its id), or why not; and the terms left off.</summary>
    public sealed record Imported(PostView? Post, bool AlreadyHere, string? Error, string? Slug = null,
        IReadOnlyList<(string Kind, string Name, string Why)>? Dropped = null);

    /// <summary>
    /// The import's way in (docs/specs/import.md): a live post with the source's own id, its first revision written by
    /// <paramref name="authorAccountId"/>, its terms made as needed, published when the source published it. Its history
    /// starts here: one revision and a <c>post_imported</c> event, which bylines count as its publication and which
    /// announcing, notifications and search pings leave alone (the post isn't new). A post already here by that id is
    /// left as it is, so an import can run again.
    /// </summary>
    public Task<Imported> ImportAsync(Guid blogId, Guid id, string slug, string title, string excerpt, string bodyMd,
        IReadOnlyList<TermSeed> categories, IReadOnlyList<string> tags, DateTimeOffset publishedAt, Guid authorAccountId, string source,
        CancellationToken ct)
    {
        title = title.Trim();
        if (title.Length > MaxTitle) title = title[..MaxTitle];
        if (excerpt.Length > MaxExcerpt) excerpt = excerpt[..(MaxExcerpt - 1)] + "…";
        if (Invalid(title, bodyMd, excerpt) is { } bad) return Task.FromResult(new Imported(null, false, bad.Message));
        return db.WriteAsync(async tx =>
        {
            // "Already here" means on this blog: the same source uuid on another blog of the instance is not this
            // blog's post, and a post lives on one blog only (its id is the primary key).
            var here = await tx.One<Guid?>("SELECT blog_id FROM post WHERE id = @id", new { id });
            if (here == blogId) return new Imported(null, true, null);
            if (here is not null) return new Imported(null, false, "That post's id is already on another blog on this instance; a post lives on one blog only.");
            var now = clock.GetUtcNow();
            // Terms are made as an editor would (the author's own words, not suggestions waiting for the owner), at the
            // source's own address where it has one; what can't be made is left off and named, not a failed post.
            var maker = Actor.Person(blogId, authorAccountId, Roles.Editor);
            var (termIds, dropped) = await ImportedTermsAsync(tx, maker, categories, tags, now);
            var finalSlug = await UniqueSlugAsync(tx, blogId, Slugify(slug.Length > 0 ? slug : title));
            var revisionId = Guid.CreateVersion7();
            var actor = Actors.Account(authorAccountId);
            await tx.Exec("""
                INSERT INTO post (id, blog_id, slug, status, current_revision_id, published_at, created_at, updated_at)
                VALUES (@id, @blogId, @finalSlug, 'live', @revisionId, @publishedAt, @now, @now);
                INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, excerpt, term_ids, suggested_terms, actor, created_at)
                VALUES (@revisionId, @blogId, @id, 1, @title, @bodyMd, @excerpt, @termIds, '[]'::jsonb, @actor, @now);
                """, new { id, blogId, finalSlug, revisionId, publishedAt, now, title, bodyMd, excerpt, termIds, actor });
            await tx.Event(new EventRow(blogId, "post", id, "post_imported", actor,
                new { slug = finalSlug, source_slug = slug, source, published_at = publishedAt, revision = 1 }), now);
            return new Imported((await FindAsync(tx, blogId, id.ToString(), withBody: false))!, false, null, finalSlug, dropped);
        }, ct);
    }

    /// <summary>
    /// The terms an imported post carries: a category at the source's own slug when it is an address here, else its
    /// name's; a tag under its name's slug. A name with nothing that survives into an address, one whose address another
    /// term of the kind already has, or one over the kind's cap, is left off and returned with its reason for the
    /// importer's log — the post still comes over.
    /// </summary>
    private static async Task<(Guid[] Ids, List<(string Kind, string Name, string Why)> Dropped)> ImportedTermsAsync(Tx tx, Actor maker,
        IReadOnlyList<TermSeed> categories, IReadOnlyList<string> tags, DateTimeOffset now)
    {
        var ids = new List<Guid>();
        var dropped = new List<(string Kind, string Name, string Why)>();
        await AddKind(Taxonomy.Category, categories, Taxonomy.MaxCategories);
        await AddKind(Taxonomy.Tag, tags.Select(t => new TermSeed(t)), Taxonomy.MaxTags);
        return ([.. ids], dropped);

        async Task AddKind(string kind, IEnumerable<TermSeed> seeds, int max)
        {
            var seen = new HashSet<string>();
            var count = 0;
            foreach (var seed in seeds)
            {
                var name = string.Join(' ', seed.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (name.Length > Taxonomy.MaxName) name = name[..Taxonomy.MaxName];
                var slug = kind == Taxonomy.Category && Taxonomy.IsSlug(seed.Slug) ? seed.Slug! : Taxonomy.Slug(name);
                if (name.Length == 0 || slug is null) dropped.Add((kind, seed.Name, "no-address"));
                else if (!seen.Add(slug)) dropped.Add((kind, seed.Name, "duplicate"));
                else if (count >= max) dropped.Add((kind, seed.Name, "over-cap"));
                else
                {
                    count++;
                    ids.Add(await Taxonomy.EnsureAsync(tx, maker, kind, name, "", now, slug));
                }
            }
        }
    }

    /// <summary>A new revision. Any of title, body, excerpt; the others carry over. <paramref name="baseRevision"/> is required.</summary>
    public Task<PostOutcome> EditAsync(Actor actor, string idOrSlug, Guid baseRevision, string? title, string? bodyMd, string? excerpt, CancellationToken ct,
        IReadOnlyList<string>? categories = null, IReadOnlyList<string>? tags = null) =>
        db.WriteAsync(async tx =>
        {
            if (await LockAsync(tx, actor.BlogId, idOrSlug) is not { } post) return NotFound();
            if (post.RevisionId != baseRevision) return Stale(post);
            if (post.Status == "trash") return PostOutcome.Fail("invalid_state", "The post is in the trash. Restore it first.");
            var current = (await tx.One<RevisionContent>($"{SelectContent} WHERE id = @RevisionId", post))!;
            var terms = await TermsForAsync(tx, actor, current.TermIds, current.Suggested, categories, tags, clock.GetUtcNow());
            if (terms.Error is { } termError) return termError;
            var next = new RevisionContent(title ?? current.Title, bodyMd ?? current.BodyMd, excerpt ?? current.Excerpt, terms.Ids, terms.Suggested);
            if (Invalid(next.Title, next.BodyMd, next.Excerpt) is { } bad) return bad;

            if (await WriterOwnsAsync(tx, actor, post.Id) is { } notTheirs) return notTheirs;
            var needed = LevelFor("edit", post.Status);
            if (!Allows(actor, needed) && actor.IsPerson) return Refused(actor, "edit");
            if (!Allows(actor, needed))
                return await RequestAsync(tx, actor, post, "edit", new
                {
                    title = next.Title, body_md = next.BodyMd, excerpt = next.Excerpt, term_ids = next.TermIds,
                    suggested_terms = Taxonomy.ParseSuggested(next.Suggested),
                }, needed);
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

            if (await WriterOwnsAsync(tx, actor, post.Id) is { } notTheirs) return notTheirs;
            var needed = LevelFor(action, post.Status);
            if (!Allows(actor, needed) && actor.IsPerson) return Refused(actor, action);
            if (!Allows(actor, needed))
                return await RequestAsync(tx, actor, post, action, at is { } when ? new { at = when } : new { }, needed);
            await ApplyAsync(tx, actor.BlogId, post, action, at, actor.Value, clock.GetUtcNow());
            return PostOutcome.Ok((await FindAsync(tx, actor.BlogId, post.Id.ToString(), withBody: false))!);
        }, ct);

    /// <summary>The owner puts an old revision back: a new revision with its content, so nothing in history is lost.</summary>
    public Task<PostOutcome> RestoreRevisionAsync(Actor owner, string idOrSlug, Guid revisionId, CancellationToken ct) =>
        db.WriteAsync(async tx =>
        {
            if (!owner.Edits) return PostOutcome.Fail("forbidden", "Only the owner or an editor puts an old revision back.");
            if (await LockAsync(tx, owner.BlogId, idOrSlug) is not { } post) return NotFound();
            var old = await tx.One<RevisionContent>($"{SelectContent} WHERE blog_id = @BlogId AND post_id = @postId AND id = @revisionId",
                new { owner.BlogId, postId = post.Id, revisionId });
            if (old is null) return PostOutcome.Fail("not_found", "That revision isn't one of this post's.");
            await SaveRevisionAsync(tx, owner.BlogId, post.Id, old, owner.Value, clock.GetUtcNow(), "revision_restored", new { from = revisionId });
            return PostOutcome.Ok((await FindAsync(tx, owner.BlogId, post.Id.ToString(), withBody: true))!);
        }, ct);

    // ------------------------------------------------------------------------------------- approval requests

    /// <summary>An agent's own request, for get_request. Another connection's reads as not found.</summary>
    public Task<RequestView?> RequestForAgentAsync(Guid connectionId, Guid requestId, CancellationToken ct) =>
        db.ReadAsync(tx => tx.One<RequestView>($"{SelectRequest} WHERE q.id = @requestId AND q.connection_id = @connectionId",
            new { requestId, connectionId }), ct);

    /// <summary>
    /// Who decides a request (docs/specs/members.md): whoever may do what it asks. The owner decides every one; an editor
    /// every one but trashing and restoring, which stay the owner's. A writer decides none. Needs <c>q</c> and <c>m</c>.
    /// </summary>
    internal const string DecidesSql = "(m.role = 'owner' OR (m.role = 'editor' AND q.action NOT IN ('trash', 'restore')))";

    /// <summary>The requests waiting for this account to decide them, on every blog it's on.</summary>
    public Task<IReadOnlyList<RequestView>> WaitingForAsync(Guid accountId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<RequestView>>(async tx => [.. await tx.Query<RequestView>($"""
            {SelectRequest}
            JOIN blog_member m ON m.blog_id = q.blog_id AND m.account_id = @accountId
            WHERE q.state = 'waiting' AND {DecidesSql} ORDER BY q.created_at
            """, new { accountId })], ct);

    public const int MaxDecisionNote = 2000;

    /// <summary>
    /// The decision of the owner or an editor who may decide it (<see cref="DecidesSql"/>): <c>approve</c>, <c>decline</c>, or <c>changes</c> (ask for changes, decision 0008). Approving
    /// applies exactly the pinned change, and only if the post is still at the revision the request was made against;
    /// otherwise the request turns stale and nothing applies. Any decision may carry the owner's note, which the agent
    /// reads in <c>get_request</c> and every session reads in <c>get_blog</c>; asking for changes needs one.
    /// </summary>
    public Task<PostOutcome> DecideAsync(Guid accountId, Guid requestId, string decision, string? note, CancellationToken ct,
        OwnerEdits? edits = null)
    {
        if (edits is { IsEmpty: true }) edits = null;
        if (edits is not null && decision != "approve")
            return Task.FromResult(PostOutcome.Fail("invalid_argument", "Edits go with Approve; to send the agent back to change it, ask for changes."));
        // Checked before anything is written: a refusal returned from inside the transaction would still commit what came before it.
        if (edits is not null && Invalid(edits.Title ?? "kept", edits.BodyMd ?? "", edits.Excerpt) is { } badEdits)
            return Task.FromResult(badEdits);
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (decision is not ("approve" or "decline" or "changes"))
            return Task.FromResult(PostOutcome.Fail("invalid_argument", "The decision is approve, decline or changes."));
        if (decision == "changes" && note is null)
            return Task.FromResult(PostOutcome.Fail("invalid_argument", "Asking for changes needs a note: say what to change."));
        if (note?.Length > MaxDecisionNote)
            return Task.FromResult(PostOutcome.Fail("invalid_argument", $"A note is at most {MaxDecisionNote} characters."));
        var approve = decision == "approve";
        return db.WriteAsync(async tx =>
        {
            var q = await tx.One<RequestRow>($"""
                SELECT q.id, q.blog_id, q.connection_id, q.action, q.post_id, q.pinned_revision_id, q.payload::text AS payload, q.state
                FROM approval_request q
                JOIN blog_member m ON m.blog_id = q.blog_id AND m.account_id = @accountId
                WHERE q.id = @requestId AND {DecidesSql} FOR UPDATE OF q
                """, new { accountId, requestId });
            if (q is null) return PostOutcome.Fail("not_found", "No such request on your blogs.");
            if (q.State != "waiting") return PostOutcome.Fail("invalid_state", $"This request is already {q.State}.");
            var now = clock.GetUtcNow();
            var owner = Actors.Account(accountId);

            if (!approve)
            {
                var state = decision == "changes" ? "changes_requested" : "declined";
                await SetStateAsync(tx, q, state, accountId, now, note);
                if (note is not null)
                    await AgentBlog.Feedback.OwnerFeedback.InsertAsync(tx, q.BlogId, state, note, q.PostId, q.Id, accountId, now);
                await tx.Event(new EventRow(q.BlogId, "approval_request", q.Id,
                    decision == "changes" ? "approval_changes_requested" : "approval_declined", owner, new { q.Action }), now);
                return new PostOutcome(PostOutcome.Done, Request: await RequestViewAsync(tx, q.Id));
            }

            var post = (await LockAsync(tx, q.BlogId, q.PostId.ToString()))!;
            var payload = System.Text.Json.JsonDocument.Parse(q.Payload).RootElement;
            DateTimeOffset? at = payload.TryGetProperty("at", out var a) ? a.GetDateTimeOffset() : null;
            if (post.RevisionId != q.PinnedRevisionId || (q.Action == "edit" ? post.Status == "trash" : CheckState(q.Action, post.Status, at, now) is not null))
            {
                await SetStateAsync(tx, q, "stale", accountId, now);
                await tx.Event(new EventRow(q.BlogId, "approval_request", q.Id, "approval_stale", owner, new { q.Action }), now);
                return PostOutcome.Fail("stale", "The post changed since the agent asked, so this request no longer applies. Nothing was changed.",
                    post.RevisionId);
            }

            if (edits is not null && q.Action is not ("edit" or "publish" or "schedule"))
                return PostOutcome.Fail("invalid_argument", "Approve with edits works on an edit, a publish or a schedule; there's no text to change here.");
            if (q.Action == "edit")
            {
                // The words are the agent's: the revision names the connection; the event names who approved it.
                // Terms ride with the edit as they were resolved when the agent asked (terms are never deleted, so the ids hold).
                var was = (await tx.One<RevisionContent>($"{SelectContent} WHERE id = @RevisionId", post))!;
                var content = new RevisionContent(payload.GetProperty("title").GetString()!, payload.GetProperty("body_md").GetString()!,
                    payload.GetProperty("excerpt").GetString()!,
                    payload.TryGetProperty("term_ids", out var ids) ? [.. ids.EnumerateArray().Select(i => i.GetGuid())] : was.TermIds,
                    payload.TryGetProperty("suggested_terms", out var sug) ? sug.GetRawText() : was.Suggested);
                await SaveRevisionAsync(tx, q.BlogId, post.Id, content, Actors.Connection(q.ConnectionId), now, "revision_saved",
                    new { approved_by = owner });
                // Approve with edits: the owner's changes are their own revision on top, so each author keeps their words.
                if (edits is not null && await OwnerRevisionAsync(tx, q.BlogId, post.Id, content, edits, owner, now) is { } bad) return bad;
            }
            else
            {
                if (edits is not null)
                {
                    // The owner's fix first, as their own revision; then what the agent asked for, on that revision.
                    var current = (await tx.One<RevisionContent>($"{SelectContent} WHERE id = @RevisionId", post))!;
                    if (await OwnerRevisionAsync(tx, q.BlogId, post.Id, current, edits, owner, now) is { } bad) return bad;
                    post = (await LockAsync(tx, q.BlogId, post.Id.ToString()))!;
                }
                await ApplyAsync(tx, q.BlogId, post, q.Action, at, owner, now, requestedBy: Actors.Connection(q.ConnectionId));
            }
            await SetStateAsync(tx, q, "approved", accountId, now, note);
            if (note is not null)
                await AgentBlog.Feedback.OwnerFeedback.InsertAsync(tx, q.BlogId, "approved", note, q.PostId, q.Id, accountId, now);
            await tx.Event(new EventRow(q.BlogId, "approval_request", q.Id, "approval_approved", owner, new { q.Action }), now);
            return new PostOutcome(PostOutcome.Done, await FindAsync(tx, q.BlogId, post.Id.ToString(), withBody: false), await RequestViewAsync(tx, q.Id));
        }, ct);
    }

    /// <summary>The owner's edits over <paramref name="basis"/> as a revision of their own; a refusal if the result isn't valid.</summary>
    private static async Task<PostOutcome?> OwnerRevisionAsync(Tx tx, Guid blogId, Guid postId, RevisionContent basis, OwnerEdits edits, string owner,
        DateTimeOffset now)
    {
        var next = basis with { Title = edits.Title ?? basis.Title, BodyMd = edits.BodyMd ?? basis.BodyMd, Excerpt = edits.Excerpt ?? basis.Excerpt };
        if (Invalid(next.Title, next.BodyMd, next.Excerpt) is { } bad) return bad;
        if (next == basis) return null;
        await SaveRevisionAsync(tx, blogId, postId, next, owner, now, "revision_saved", new { approved_with_edits = true });
        return null;
    }

    /// <summary>
    /// Why approving this request would no longer apply, in the owner's words, or null when it still would: the post
    /// moved on to another revision, or its state no longer allows the action (already live, in the trash, a schedule
    /// time that has passed). The same rule <see cref="DecideAsync"/> applies, so the app never offers an approval that
    /// can't happen.
    /// </summary>
    internal static string? WhyNotApplies(RequestView q, DateTimeOffset now)
    {
        if (q.CurrentRevisionId != q.PinnedRevisionId) return "The post changed since the agent asked, so approving won't apply anything.";
        if (q.Action == "edit")
            return q.CurrentStatus == "trash" ? "The post is in the trash now, so this edit no longer applies." : null;
        DateTimeOffset? at = null;
        using (var payload = System.Text.Json.JsonDocument.Parse(q.Payload))
            if (payload.RootElement.TryGetProperty("at", out var a)) at = a.GetDateTimeOffset();
        return CheckState(q.Action, q.CurrentStatus, at, now) switch
        {
            null => null,
            _ when q.Action == "schedule" && at <= now => "The time the agent asked for has passed, so this schedule no longer applies.",
            _ => $"The post is {StatusWords(q.CurrentStatus)} now, so this no longer applies.",
        };
    }

    private static string StatusWords(string status) => status switch
    {
        "review" => "in review",
        "trash" => "in the trash",
        _ => status,
    };

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
            INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, excerpt, term_ids, suggested_terms, actor, created_at)
            SELECT @revisionId, @blogId, @postId, coalesce(max(number), 0) + 1, @Title, @BodyMd, @Excerpt, @TermIds, @Suggested::jsonb, @actor, @now
            FROM post_revision WHERE post_id = @postId
            RETURNING number
            """, new { revisionId, blogId, postId, c.Title, c.BodyMd, c.Excerpt, c.TermIds, c.Suggested, actor, now });
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

    private static Task SetStateAsync(Tx tx, RequestRow q, string state, Guid accountId, DateTimeOffset now, string? note = null) =>
        tx.Exec("UPDATE approval_request SET state = @state, decided_at = @now, decided_by = @accountId, decision_note = coalesce(@note, decision_note) WHERE id = @Id",
            new { state, now, accountId, q.Id, note });

    private static Task<RequestView?> RequestViewAsync(Tx tx, Guid id) => tx.One<RequestView>($"{SelectRequest} WHERE q.id = @id", new { id });

    private static readonly string SelectRequest = $"""
        SELECT q.id, q.blog_id, q.post_id, r.title AS post_title, q.action, q.pinned_revision_id, q.state, c.label AS connection_label,
               q.created_at, q.decided_at, q.payload::text AS payload, p.current_revision_id,
               r.body_md AS current_body_md, r.excerpt AS current_excerpt,
               {TermsOf("r.term_ids")} AS current_terms_json,
               {TermsOf("ARRAY(SELECT e::uuid FROM jsonb_array_elements_text(coalesce(q.payload->'term_ids', '[]'::jsonb)) e)")} AS proposed_terms_json,
               p.status AS current_status, q.decision_note,
               -- Who decided (the owner's words are instruction to an agent; an editor's are not) and whose agent asked.
               coalesce(q.decided_by = o.account_id, false) AS decided_by_owner, da.display_name AS decided_by_name,
               pa.display_name AS agent_person, c.created_by = o.account_id AS agent_person_is_owner
        FROM approval_request q
        JOIN post p ON p.id = q.post_id
        JOIN post_revision r ON r.id = p.current_revision_id
        JOIN connection c ON c.id = q.connection_id
        JOIN account pa ON pa.id = c.created_by
        LEFT JOIN blog_member o ON o.blog_id = q.blog_id AND o.role = 'owner'
        LEFT JOIN account da ON da.id = q.decided_by
        """;

    private static string SelectView(bool withBody) => $"""
        SELECT p.id, p.slug, p.status, r.id AS revision_id, r.number AS revision_number, r.title, r.excerpt,
               {(withBody ? "r.body_md" : "NULL::text AS body_md")}, p.publish_at, p.published_at, p.updated_at,
               {TermsOf("r.term_ids")} AS terms_json, r.suggested_terms::text AS suggested_json
        FROM post p JOIN post_revision r ON r.id = p.current_revision_id
        """;

    /// <summary>The terms an array of ids names, as JSON (<see cref="TermRef"/>), categories first, then by name.</summary>
    internal static string TermsOf(string ids) => $"""
        (SELECT coalesce(json_agg(json_build_object('id', t.id, 'kind', t.kind, 'slug', t.slug, 'name', t.name)
                                  ORDER BY t.kind, lower(t.name)), '[]'::json)
         FROM term t WHERE t.id = ANY({ids}))::text
        """;

    private const string SelectContent = "SELECT title, body_md, excerpt, term_ids, suggested_terms::text AS suggested FROM post_revision";

    /// <summary>
    /// The terms the next revision carries: unchanged when neither list is given; otherwise each given kind is replaced
    /// by those names (existing terms, new ones created at Publish and up, the rest suggested to the owner).
    /// </summary>
    private static async Task<(Guid[] Ids, string Suggested, PostOutcome? Error)> TermsForAsync(Tx tx, Actor actor, Guid[] currentIds,
        string currentSuggested, IReadOnlyList<string>? categories, IReadOnlyList<string>? tags, DateTimeOffset now)
    {
        if (categories is null && tags is null) return (currentIds, currentSuggested, null);
        var mayCreate = Allows(actor, Levels.Publish);
        var kinds = await Taxonomy.KindsAsync(tx, actor.BlogId, currentIds);
        var ids = currentIds.ToList();
        var suggested = Taxonomy.ParseSuggested(currentSuggested);
        foreach (var (kind, names) in new[] { (Taxonomy.Category, categories), (Taxonomy.Tag, tags) })
        {
            if (names is null) continue;
            var (found, more, error) = await Taxonomy.ResolveAsync(tx, actor, kind, names, mayCreate, now);
            if (error is not null) return ([], "[]", PostOutcome.Fail("invalid_argument", error));
            ids.RemoveAll(id => kinds.GetValueOrDefault(id) == kind);
            ids.AddRange(found);
            suggested.RemoveAll(x => x.Kind == kind);
            suggested.AddRange(more);
        }
        return ([.. ids.Distinct()], Taxonomy.Serialize(suggested), null);
    }

    /// <summary>
    /// The owner accepts or dismisses a term an agent suggested: a new revision with the term on it (created if needed)
    /// or without the suggestion. Every save is a revision, this one too.
    /// </summary>
    public Task<PostOutcome> DecideSuggestionAsync(Actor owner, string idOrSlug, string kind, string name, bool accept, CancellationToken ct) =>
        db.WriteAsync(async tx =>
        {
            if (!owner.Edits) return PostOutcome.Fail("forbidden", "Only the owner or an editor decides suggested terms.");
            if (await LockAsync(tx, owner.BlogId, idOrSlug) is not { } post) return NotFound();
            var current = (await tx.One<RevisionContent>($"{SelectContent} WHERE id = @RevisionId", post))!;
            var suggested = Taxonomy.ParseSuggested(current.Suggested);
            var slug = Taxonomy.Slug(name);
            var match = suggested.FirstOrDefault(x => x.Kind == kind && Taxonomy.Slug(x.Name) == slug);
            if (match is null) return PostOutcome.Fail("not_found", $"\"{name}\" isn't a suggested {kind} on this post.");
            suggested.Remove(match);
            var now = clock.GetUtcNow();
            var ids = current.TermIds.ToList();
            if (accept)
            {
                var id = await Taxonomy.EnsureAsync(tx, owner, kind, match.Name, "", now);
                if (!ids.Contains(id)) ids.Add(id);
            }
            await SaveRevisionAsync(tx, owner.BlogId, post.Id, current with { TermIds = [.. ids], Suggested = Taxonomy.Serialize(suggested) },
                owner.Value, now, "revision_saved", new { suggestion = accept ? "accepted" : "dismissed", kind, name = match.Name });
            return PostOutcome.Ok((await FindAsync(tx, owner.BlogId, post.Id.ToString(), withBody: true))!);
        }, ct);

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

    private sealed record RevisionContent(string Title, string BodyMd, string Excerpt, Guid[] TermIds, string Suggested)
    {
        /// <summary>For Dapper: it reports a uuid[] column as System.Array, which no constructor matches, so it fills properties.</summary>
        public RevisionContent() : this("", "", "", [], "[]") { }
    }

    internal sealed record PostRow(Guid Id, string Status, Guid RevisionId, int RevisionNumber);

    private sealed record RequestRow(Guid Id, Guid BlogId, Guid ConnectionId, string Action, Guid PostId, Guid PinnedRevisionId,
        string Payload, string State);
}
