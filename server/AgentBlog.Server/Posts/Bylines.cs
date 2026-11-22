using AgentBlog.Database;

namespace AgentBlog.Posts;

/// <summary>One name on a post: who, what kind (<c>person</c> or <c>agent</c>), and what they did.</summary>
public sealed record Credit(string Name, string Kind, string Role, string Actor);

/// <summary>A post's byline: the sentence, and each name behind it.</summary>
public sealed record Byline(string Text, IReadOnlyList<Credit> Credits)
{
    public IEnumerable<Credit> People => Credits.Where(c => c.Kind == "person").DistinctBy(c => c.Actor);
    public IEnumerable<Credit> Agents => Credits.Where(c => c.Kind == "agent").DistinctBy(c => c.Actor);
    public IEnumerable<Credit> Writers => Credits.Where(c => c.Role == "wrote");
}

/// <summary>
/// Honest bylines (docs/specs/bylines.md): who wrote a post and who let it out, derived from its revisions, its first
/// publication and the approvals, never typed in. Fixed templates, no model.
/// </summary>
public sealed class Bylines(Db db)
{
    /// <summary>Whether the blog shows bylines (on unless the owner turned them off).</summary>
    public Task<bool> OnAsync(Guid blogId, CancellationToken ct) => db.ReadAsync(async tx =>
        await tx.One<bool?>("SELECT (settings->>'bylines')::boolean FROM blog WHERE id = @blogId", new { blogId }) ?? true, ct);

    private sealed record RevisionFact(Guid PostId, int Number, string Actor, DateTime CreatedAt, string? Kind, string? Extra);
    private sealed record Published(Guid PostId, string Actor, string? ScheduledBy, DateTime At);

    /// <summary>The byline of each post in <paramref name="postIds"/> (one query per kind, for a feed of many).</summary>
    public Task<Dictionary<Guid, Byline>> ForPostsAsync(Guid blogId, IReadOnlyCollection<Guid> postIds, CancellationToken ct) => db.ReadAsync(async tx =>
    {
        var ids = postIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, Byline>();
        // Each revision with the event that saved it: what kind of save, and its extra (approved_by, a suggestion).
        var revisions = (await tx.Query<RevisionFact>("""
            SELECT r.post_id, r.number, r.actor, r.created_at, e.kind, (e.payload->'extra')::text AS extra
            FROM post_revision r
            LEFT JOIN LATERAL (
                SELECT kind, payload FROM event
                WHERE target_type = 'post' AND target_id = r.post_id AND kind IN ('revision_saved', 'revision_restored')
                  AND (payload->>'revision')::int = r.number
                ORDER BY id LIMIT 1) e ON true
            WHERE r.blog_id = @blogId AND r.post_id = ANY (@ids)
            ORDER BY r.post_id, r.number
            """, new { blogId, ids })).ToList();
        var published = (await tx.Query<Published>("""
            SELECT DISTINCT ON (target_id) target_id AS post_id, actor, payload->>'scheduled_by' AS scheduled_by, at
            FROM event WHERE blog_id = @blogId AND target_type = 'post' AND kind = 'post_published' AND target_id = ANY (@ids)
            ORDER BY target_id, id
            """, new { blogId, ids })).ToDictionary(p => p.PostId);
        // People by their account's name; the owner's "Your name on posts" is the owner's alone. Someone removed from the
        // blog is still named: the account outlives the membership (docs/specs/members.md).
        var accountIds = revisions.Select(r => r.Actor).Concat(published.Values.SelectMany(p => new[] { p.Actor, p.ScheduledBy ?? "" }))
            .Where(a => a.StartsWith("account:", StringComparison.Ordinal)).Select(a => Guid.TryParse(a[8..], out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).Distinct().ToArray();
        var people = (await tx.Query<(Guid Id, string Name)>("""
            SELECT a.id, CASE WHEN m.role = 'owner' THEN coalesce(nullif(b.settings->>'author_name', ''), a.display_name) ELSE a.display_name END
            FROM account a JOIN blog b ON b.id = @blogId
            LEFT JOIN blog_member m ON m.blog_id = b.id AND m.account_id = a.id
            WHERE a.id = ANY (@accountIds)
            """, new { blogId, accountIds })).ToDictionary(p => Actors.Account(p.Id), p => p.Name);
        var agents = (await tx.Query<(Guid Id, string Name)>("""
            SELECT id, coalesce(byline_name, nullif(split_part(label, ' · ', 1), ''), label) FROM connection WHERE blog_id = @blogId
            """, new { blogId })).ToDictionary(c => Actors.Connection(c.Id), c => c.Name);

        string NameOf(string actor) => agents.TryGetValue(actor, out var n) ? n
            : people.TryGetValue(actor, out var p) ? p
            : actor.StartsWith("account:", StringComparison.Ordinal) ? "a removed account" : "the blog";
        string KindOf(string actor) => actor.StartsWith("connection:", StringComparison.Ordinal) ? "agent" : "person";

        var result = new Dictionary<Guid, Byline>();
        foreach (var id in ids)
            result[id] = Build(revisions.Where(r => r.PostId == id).ToList(), published.GetValueOrDefault(id), NameOf, KindOf);
        return result;
    }, ct);

    public async Task<Byline?> ForPostAsync(Guid blogId, Guid postId, CancellationToken ct) =>
        (await ForPostsAsync(blogId, [postId], ct)).GetValueOrDefault(postId);

    private static Byline Build(List<RevisionFact> revisions, Published? published, Func<string, string> nameOf, Func<string, string> kindOf)
    {
        // Writers: a revision with new words. A restore copies old words; a tag suggestion decided changes no words.
        static bool Wrote(RevisionFact r) => r.Kind != "revision_restored" && !(r.Extra?.Contains("\"suggestion\"", StringComparison.Ordinal) ?? false);
        var writers = revisions.Where(Wrote).Select(r => r.Actor).Where(a => a != Actors.System).Distinct().ToList();
        var credits = writers.Select(a => new Credit(nameOf(a), kindOf(a), "wrote", a)).ToList();
        string Names(IEnumerable<string> actors) => List(actors.Select(nameOf).Distinct().ToList());

        if (published is null)
        {
            var draft = writers.Count == 0 ? "" : writers.All(a => kindOf(a) == "person") ? $"By {Names(writers)}" : $"Written by {Names(writers)}";
            return new Byline(draft.Length == 0 ? "Not published yet" : draft + " · not published yet", credits);
        }

        // Who let it out: an account (directly, by approving, or by its schedule), else the agent that published.
        var by = published.Actor == Actors.System ? published.ScheduledBy ?? Actors.System : published.Actor;
        var ownerLetOut = by.StartsWith("account:", StringComparison.Ordinal);
        var changedSince = revisions
            .Where(r => r.CreatedAt > published.At && Wrote(r) && kindOf(r.Actor) == "agent"
                        && !(r.Extra?.Contains("\"approved_by\"", StringComparison.Ordinal) ?? false))
            .Select(r => r.Actor).Distinct().ToList();

        string text;
        if (writers.All(a => kindOf(a) == "person") && ownerLetOut)
        {
            text = $"By {Names(writers.Count > 0 ? writers : [by])}";
        }
        else if (ownerLetOut)
        {
            text = $"Written by {Names(writers)} · approved by {nameOf(by)}";
            credits.Add(new Credit(nameOf(by), "person", "approved", by));
        }
        else
        {
            text = writers.Count == 1 && writers[0] == by ? $"Written and published by {nameOf(by)}" : $"Written by {Names(writers)} · published by {nameOf(by)}";
            credits.Add(new Credit(nameOf(by), kindOf(by), "published", by));
        }
        if (changedSince.Count > 0)
        {
            text += $" · changed since by {Names(changedSince)}";
            credits.AddRange(changedSince.Select(a => new Credit(nameOf(a), "agent", "changed", a)));
        }
        return new Byline(text, credits);
    }

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string List(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>For a theme: the sentence and each name, its kind and what it did.</summary>
    public static Dictionary<string, object?> Model(Byline b) => new()
    {
        ["text"] = b.Text,
        ["credits"] = b.Credits.Select(c => new Dictionary<string, object?> { ["name"] = c.Name, ["kind"] = c.Kind, ["role"] = c.Role }).ToList(),
    };

    public static object Json(Byline b) => new
    {
        text = b.Text,
        credits = b.Credits.Select(c => new { name = c.Name, kind = c.Kind, role = c.Role }),
    };
}
