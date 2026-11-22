using System.Text.Json;
using AgentBlog.Database;
using AgentBlog.Posts;

namespace AgentBlog.Feedback;

/// <summary>The brief as the owner wrote it. Empty fields are "".</summary>
public sealed record Brief(string Audience, string Voice, string Avoid, IReadOnlyList<Guid> Examples, DateTime? UpdatedAt)
{
    public bool IsEmpty => Audience.Length == 0 && Voice.Length == 0 && Avoid.Length == 0 && Examples.Count == 0;
}

/// <summary>A post the brief points at as showing the voice.</summary>
public sealed record BriefExample(Guid Id, string Slug, string Title, string Status);

public sealed record NoteView(Guid Id, string Kind, string Note, Guid? PostId, string? PostTitle, Guid? RequestId, string? Action,
    DateTime CreatedAt, DateTime? RetiredAt);

/// <summary>
/// The brief and the owner's notes (decision 0008, docs/specs/feedback.md): the one blog text an agent follows as
/// instruction, because the owner wrote it. Only the owner's API writes here; agents only read, through
/// <c>get_blog</c>, <c>read_post</c> and <c>get_request</c>, where every piece is marked <c>author: owner</c>.
/// </summary>
public sealed class OwnerFeedback(Db db, TimeProvider clock)
{
    public const int MaxAudience = 500;
    public const int MaxVoice = 2000;
    public const int MaxAvoid = 2000;
    public const int MaxExamples = 3;
    public const int MaxNote = 2000;
    /// <summary>How many of the most recent notes every session gets in <c>get_blog</c>.</summary>
    public const int RecentNotes = 10;

    private sealed record StoredBrief(string? Audience, string? Voice, string? Avoid, Guid[]? Examples, DateTime? UpdatedAt);

    // ----------------------------------------------------------------------------------------------------- brief

    public Task<(Brief Brief, IReadOnlyList<BriefExample> Examples)> BriefAsync(Guid blogId, CancellationToken ct) =>
        db.ReadAsync(async tx =>
        {
            var json = await tx.One<string>("SELECT brief::text FROM blog WHERE id = @blogId", new { blogId }) ?? "{}";
            var s = JsonSerializer.Deserialize<StoredBrief>(json, Json.Options)!;
            var brief = new Brief(s.Audience ?? "", s.Voice ?? "", s.Avoid ?? "", s.Examples ?? [], s.UpdatedAt);
            IReadOnlyList<BriefExample> examples = brief.Examples.Count == 0 ? [] : [.. await tx.Query<BriefExample>("""
                SELECT p.id, p.slug, r.title, p.status FROM post p JOIN post_revision r ON r.id = p.current_revision_id
                WHERE p.blog_id = @blogId AND p.id = ANY(@ids) AND p.status <> 'trash'
                ORDER BY array_position(@ids, p.id)
                """, new { blogId, ids = brief.Examples.ToArray() })];
            return (brief, examples);
        }, ct);

    /// <summary>Replaces the brief (it's one form). Null on success, otherwise why not.</summary>
    public async Task<string?> SaveBriefAsync(Actor owner, string? audience, string? voice, string? avoid, IReadOnlyList<Guid>? examples,
        CancellationToken ct)
    {
        if (!owner.IsOwner) return "Only the owner writes the brief.";
        audience = (audience ?? "").Trim();
        voice = (voice ?? "").Trim();
        avoid = (avoid ?? "").Trim();
        var ids = (examples ?? []).Distinct().ToArray();
        if (audience.Length > MaxAudience) return $"Who it's for is at most {MaxAudience} characters.";
        if (voice.Length > MaxVoice) return $"The voice is at most {MaxVoice} characters.";
        if (avoid.Length > MaxAvoid) return $"What to avoid is at most {MaxAvoid} characters.";
        if (ids.Length > MaxExamples) return $"Pick at most {MaxExamples} example posts.";

        return await db.WriteAsync(async tx =>
        {
            var found = await tx.One<int>("SELECT count(*)::int FROM post WHERE blog_id = @BlogId AND id = ANY(@ids) AND status <> 'trash'",
                new { owner.BlogId, ids });
            if (found != ids.Length) return "An example post isn't one of this blog's (or it's in the trash).";
            var before = JsonSerializer.Deserialize<StoredBrief>(
                await tx.One<string>("SELECT brief::text FROM blog WHERE id = @BlogId FOR UPDATE", owner) ?? "{}", Json.Options)!;
            var changed = new List<string>();
            if ((before.Audience ?? "") != audience) changed.Add("audience");
            if ((before.Voice ?? "") != voice) changed.Add("voice");
            if ((before.Avoid ?? "") != avoid) changed.Add("avoid");
            if (!(before.Examples ?? []).SequenceEqual(ids)) changed.Add("examples");
            if (changed.Count == 0) return null;
            var now = clock.GetUtcNow();
            await tx.Exec("UPDATE blog SET brief = @brief::jsonb WHERE id = @BlogId", new
            {
                owner.BlogId,
                brief = JsonSerializer.Serialize(new StoredBrief(audience, voice, avoid, ids, now.UtcDateTime), Json.Options),
            });
            await tx.Event(new EventRow(owner.BlogId, "blog", owner.BlogId, "brief_changed", owner.Value, new { changed }), now);
            return (string?)null;
        }, ct);
    }

    // ----------------------------------------------------------------------------------------------------- notes

    private const string SelectNote = """
        SELECT n.id, n.kind, n.note, n.post_id, r.title AS post_title, n.request_id, q.action, n.created_at, n.retired_at
        FROM owner_note n
        LEFT JOIN post p ON p.id = n.post_id
        LEFT JOIN post_revision r ON r.id = p.current_revision_id
        LEFT JOIN approval_request q ON q.id = n.request_id
        """;

    /// <summary>Notes, newest first: only the ones still in force (<paramref name="current"/>), or all; about one post, or any.</summary>
    public Task<IReadOnlyList<NoteView>> NotesAsync(Guid blogId, bool current, Guid? postId, int limit, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<NoteView>>(async tx => [.. await tx.Query<NoteView>($"""
            {SelectNote}
            WHERE n.blog_id = @blogId AND (NOT @current OR n.retired_at IS NULL) AND (@postId::uuid IS NULL OR n.post_id = @postId)
            ORDER BY n.created_at DESC, n.id
            LIMIT @limit
            """, new { blogId, current, postId, limit = Math.Clamp(limit, 1, 200) })], ct);

    /// <summary>A note to every agent, optionally about one post.</summary>
    public async Task<(NoteView? Note, string? Error)> AddNoteAsync(Actor owner, string? note, Guid? postId, CancellationToken ct)
    {
        if (!owner.IsOwner) return (null, "Only the owner writes notes to agents.");
        note = (note ?? "").Trim();
        if (note.Length is < 1 or > MaxNote) return (null, $"A note is 1–{MaxNote} characters.");
        var id = await db.WriteAsync(async tx =>
        {
            if (postId is { } p && await tx.One<Guid?>("SELECT id FROM post WHERE blog_id = @BlogId AND id = @p", new { owner.BlogId, p }) is null)
                return (Guid?)null;
            return await InsertAsync(tx, owner.BlogId, "general", note, postId, null, owner.AccountId!.Value, clock.GetUtcNow());
        }, ct);
        if (id is null) return (null, "That post isn't one of this blog's.");
        return ((await NotesAsync(owner.BlogId, current: false, null, 200, ct)).First(n => n.Id == id), null);
    }

    /// <summary>A note that no longer holds stops being sent to agents. It stays in the log.</summary>
    public Task<bool> RetireAsync(Actor owner, Guid noteId, CancellationToken ct) => db.WriteAsync(async tx =>
    {
        var now = clock.GetUtcNow();
        var n = await tx.Exec("UPDATE owner_note SET retired_at = @now WHERE blog_id = @BlogId AND id = @noteId AND retired_at IS NULL",
            new { owner.BlogId, noteId, now });
        if (n == 1) await tx.Event(new EventRow(owner.BlogId, "owner_note", noteId, "note_retired", owner.Value, null), now);
        return n == 1;
    }, ct);

    /// <summary>Writes one note (inside the caller's transaction, which holds the event lock).</summary>
    internal static async Task<Guid> InsertAsync(Tx tx, Guid blogId, string kind, string note, Guid? postId, Guid? requestId, Guid accountId,
        DateTimeOffset now)
    {
        var id = Guid.CreateVersion7();
        await tx.Exec("""
            INSERT INTO owner_note (id, blog_id, kind, note, post_id, request_id, written_by, created_at)
            VALUES (@id, @blogId, @kind, @note, @postId, @requestId, @accountId, @now)
            """, new { id, blogId, kind, note, postId, requestId, accountId, now });
        await tx.Event(new EventRow(blogId, "owner_note", id, "note_written", Actors.Account(accountId), new { kind, post = postId }), now);
        return id;
    }

    /// <summary>A note as an agent receives it: marked as the owner's words.</summary>
    public static object ForAgent(NoteView n) => new
    {
        author = "owner",
        note = n.Note,
        kind = n.Kind,
        about_post = n.PostId is { } p ? new { id = p, title = n.PostTitle } : null,
        about_request = n.RequestId is { } q ? new { id = q, action = n.Action } : null,
        at = n.CreatedAt,
    };
}
