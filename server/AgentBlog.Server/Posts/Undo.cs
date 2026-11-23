using AgentBlog.Database;
using AgentBlog.Media;

namespace AgentBlog.Posts;

/// <summary>What undoing one agent action would do, in the owner's words, or why it can't now.</summary>
public sealed record UndoPlan(string Label, string? WhyNot, bool Done = false);

/// <summary>
/// Undo from History (docs/specs/history.md, M3): every action an agent took on the blog's posts and pictures has its
/// inverse, one tap away, judged against the post as it is now (the same rule as approvals: if the post moved on, the
/// undo no longer applies and says why). An undo is an ordinary owner action through the same core, so it is itself a
/// revision or a state change with an event, and nothing is lost: undoing an edit puts the earlier words back as a new
/// revision. Each undo is recorded (<c>action_undone</c>), so History shows it as done.
/// </summary>
public sealed class Undo(Db db, PostService posts, MediaService media, TimeProvider clock)
{
    private static readonly string[] Kinds =
        ["post_created", "revision_saved", "post_published", "post_scheduled", "post_unpublished", "post_trashed", "post_restored", "media_added",
         "media_trashed", "blog_settings_changed"];

    private sealed record Row(long Id, Guid BlogId, string Kind, string Actor, string TargetType, Guid TargetId, int? Revision,
        string? PostStatus, int? CurrentRevision, DateTime? PublishedAt, string? MediaStatus, bool Undone, string Payload,
        string? BlogTitle, string? BlogDescription);

    private static Task<IReadOnlyList<Row>> RowsAsync(Tx tx, Guid blogId, long[] ids) => tx.Query<Row>("""
        SELECT e.id, e.blog_id, e.kind, e.actor, e.target_type, e.target_id, (e.payload->>'revision')::int AS revision,
               p.status AS post_status, r.number AS current_revision, p.published_at, m.status AS media_status,
               EXISTS (SELECT 1 FROM event u WHERE u.blog_id = e.blog_id AND u.kind = 'action_undone' AND (u.payload->>'event')::bigint = e.id) AS undone,
               e.payload::text AS payload, b.title AS blog_title, b.description AS blog_description
        FROM event e
        LEFT JOIN blog b ON e.target_type = 'blog' AND b.id = e.target_id
        LEFT JOIN post p ON e.target_type = 'post' AND p.id = e.target_id
        LEFT JOIN post_revision r ON r.id = p.current_revision_id
        LEFT JOIN media m ON e.target_type = 'media' AND m.id = e.target_id
        WHERE e.blog_id = @blogId AND e.id = ANY (@ids) AND e.actor LIKE 'connection:%' AND e.kind = ANY (@Kinds)
        """, new { blogId, ids, Kinds }).ContinueWith(t => (IReadOnlyList<Row>)[.. t.Result]);

    /// <summary>The undo for each of these events an agent caused; events nobody can undo (or people caused) have none.</summary>
    public Task<Dictionary<long, UndoPlan>> PlansAsync(Guid blogId, IReadOnlyCollection<long> eventIds, CancellationToken ct) =>
        db.ReadAsync(async tx => (await RowsAsync(tx, blogId, [.. eventIds])).ToDictionary(r => r.Id, Plan), ct);

    private static UndoPlan Plan(Row r)
    {
        var plan = r.Kind switch
        {
            "post_created" => new UndoPlan("Move it to the trash", r.PostStatus == "trash" ? "It's in the trash already." : null),
            "revision_saved" when r.Revision is { } n && n > 1 => new UndoPlan($"Put revision {n - 1} back",
                r.PostStatus == "trash" ? "The post is in the trash: restore it first."
                : r.CurrentRevision != n ? $"The post changed since (it's at revision {r.CurrentRevision}): open it to pick a version."
                : null),
            "revision_saved" => new UndoPlan("Put the earlier words back", "This was the first revision: move the post to the trash instead."),
            "post_published" => new UndoPlan("Back to draft", r.PostStatus == "live" ? null : "It isn't live now."),
            "post_scheduled" => new UndoPlan("Back to draft", r.PostStatus == "scheduled" ? null : "It isn't scheduled now."),
            "post_unpublished" => new UndoPlan("Publish it again",
                r.PostStatus == "draft" && r.PublishedAt is not null ? null : "It isn't a draft now."),
            "post_trashed" => new UndoPlan("Restore it", r.PostStatus == "trash" ? null : "It isn't in the trash now."),
            "post_restored" => new UndoPlan("Move it back to the trash", r.PostStatus is null or "trash" ? "It's in the trash already." : null),
            "media_added" => new UndoPlan("Move the picture to the trash", r.MediaStatus == "ready" ? null : "It isn't in use now."),
            "media_trashed" => new UndoPlan("Restore the picture", r.MediaStatus == "trash" ? null : "It isn't in the trash now."),
            "blog_settings_changed" => new UndoPlan("Put the title and description back",
                SettingsTo(r) is var (title, description) && (title is null || title == r.BlogTitle) && (description is null || description == r.BlogDescription)
                    ? null : "They changed again since: set them by hand in Account."),
            _ => new UndoPlan("", "This can't be undone from here."),
        };
        return r.Undone ? plan with { WhyNot = "Undone.", Done = true } : plan;
    }

    /// <summary>What a settings change set the title and description to (null: that field wasn't changed).</summary>
    private static (string? Title, string? Description) SettingsTo(Row r) => Settings(r, "to");

    private static (string? Title, string? Description) SettingsFrom(Row r) => Settings(r, "from");

    private static (string? Title, string? Description) Settings(Row r, string side)
    {
        using var p = System.Text.Json.JsonDocument.Parse(r.Payload);
        string? Get(string field) => p.RootElement.TryGetProperty(field, out var f) && f.TryGetProperty(side, out var v) ? v.GetString() : null;
        return (Get("title"), Get("description"));
    }

    /// <summary>The owner undoes one agent action. Returns why not, or null when it's done.</summary>
    public async Task<string?> UndoAsync(Actor owner, long eventId, CancellationToken ct)
    {
        if (!owner.IsOwner) return "Only the owner undoes from History.";
        var row = (await db.ReadAsync(tx => RowsAsync(tx, owner.BlogId, [eventId]), ct)).SingleOrDefault();
        if (row is null) return "That isn't an agent's action on this blog.";
        var plan = Plan(row);
        if (plan.WhyNot is { } why) return why;

        string? failed;
        var post = row.TargetId.ToString();
        switch (row.Kind)
        {
            case "revision_saved":
                var earlier = await db.ReadAsync(tx => tx.One<Guid>("SELECT id FROM post_revision WHERE post_id = @TargetId AND number = @n",
                    new { row.TargetId, n = row.Revision!.Value - 1 }), ct);
                failed = (await posts.RestoreRevisionAsync(owner, post, earlier, ct)).Message;
                break;
            case "media_added":
                failed = (await media.TrashAsync(owner, row.TargetId, ct)).Message;
                break;
            case "media_trashed":
                failed = (await media.RestoreAsync(owner, row.TargetId, ct)).Message;
                break;
            case "blog_settings_changed":
                var from = SettingsFrom(row);
                await db.WriteAsync(async tx =>
                {
                    var changed = new Dictionary<string, object>();
                    if (from.Title is { } t) changed["title"] = new { from = row.BlogTitle, to = t };
                    if (from.Description is { } d) changed["description"] = new { from = row.BlogDescription, to = d };
                    await tx.Exec("UPDATE blog SET title = coalesce(@Title, title), description = coalesce(@Description, description) WHERE id = @BlogId",
                        new { from.Title, from.Description, owner.BlogId });
                    await tx.Event(new EventRow(owner.BlogId, "blog", owner.BlogId, "blog_settings_changed", owner.Value, changed), clock.GetUtcNow());
                    return 0;
                }, ct);
                failed = null;
                break;
            default:
                var op = row.Kind switch
                {
                    "post_created" or "post_restored" => "trash",
                    "post_published" or "post_scheduled" => "unpublish",
                    "post_unpublished" => "publish",
                    _ => "restore",
                };
                failed = (await posts.TransitionAsync(owner, post, op, null, null, ct)).Message;
                break;
        }
        if (failed is not null) return failed;
        await db.WriteAsync(async tx =>
        {
            await tx.Event(new EventRow(owner.BlogId, row.TargetType, row.TargetId, "action_undone", owner.Value,
                new { @event = row.Id, kind = row.Kind, by = row.Actor }), clock.GetUtcNow());
            return 0;
        }, ct);
        return null;
    }
}
