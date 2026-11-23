using AgentBlog.Connections;
using AgentBlog.Database;

namespace AgentBlog.Posts;

/// <summary>
/// Puts scheduled posts live when they come due, judging each schedule at that moment (decision 0001, EchoOfDreams'
/// <c>scheduled_by</c> rule): a post goes live only if whoever set the schedule could still publish it now. An agent
/// connection that was revoked or lowered below Publish, or an account that no longer owns the blog, drops it back to
/// draft, with an event saying why. Nothing else is changed.
/// </summary>
public sealed class Scheduler(Db db)
{
    public sealed record Fired(Guid PostId, string Outcome, string? Reason);

    /// <summary>One pass over what is due at <paramref name="now"/>. Concurrent passes (replicas) skip each other's rows.</summary>
    public Task<IReadOnlyList<Fired>> RunOnceAsync(DateTimeOffset now, CancellationToken ct) => db.WriteAsync<IReadOnlyList<Fired>>(async tx =>
    {
        var due = await tx.Query<Due>("""
            SELECT id, blog_id, scheduled_by, publish_at FROM post
            WHERE status = 'scheduled' AND publish_at <= @now
            ORDER BY publish_at
            LIMIT 50
            FOR UPDATE SKIP LOCKED
            """, new { now });
        var fired = new List<Fired>();
        foreach (var post in due)
        {
            var reason = await WhyNotAsync(tx, post);
            if (reason is null)
            {
                await tx.Exec("""
                    UPDATE post SET status = 'live', published_at = coalesce(published_at, publish_at),
                                    publish_at = NULL, scheduled_by = NULL, updated_at = @now
                    WHERE id = @Id
                    """, new { post.Id, now });
                await tx.Event(new EventRow(post.BlogId, "post", post.Id, "post_published", Actors.System,
                    new { via = "schedule", scheduled_by = post.ScheduledBy }), now);
                fired.Add(new Fired(post.Id, "published", null));
            }
            else
            {
                await tx.Exec("UPDATE post SET status = 'draft', publish_at = NULL, scheduled_by = NULL, updated_at = @now WHERE id = @Id",
                    new { post.Id, now });
                await tx.Event(new EventRow(post.BlogId, "post", post.Id, "schedule_dropped", Actors.System,
                    new { scheduled_by = post.ScheduledBy, reason }), now);
                fired.Add(new Fired(post.Id, "dropped", reason));
            }
        }
        return fired;
    }, ct);

    /// <summary>Null when the scheduler may still publish; otherwise why not, in words for the owner's history.</summary>
    private static async Task<string?> WhyNotAsync(Tx tx, Due post)
    {
        var (kind, id) = post.ScheduledBy.Split(':', 2) is [var k, var v] && Guid.TryParse(v, out var g) ? (k, g) : ("", Guid.Empty);
        switch (kind)
        {
            case "account":
                return await tx.One<bool>("""
                    SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @BlogId AND account_id = @id AND role IN ('owner', 'editor'))
                    """, new { post.BlogId, id })
                    ? null : "The person who scheduled it may no longer publish on this blog.";
            case "connection":
                // The level it works at now: its own, held to what its person may do (docs/specs/members.md).
                var c = await tx.One<ConnectionState>("""
                    SELECT c.level, c.revoked_at IS NOT NULL AS revoked, m.role AS person_role FROM connection c
                    LEFT JOIN blog_member m ON m.blog_id = c.blog_id AND m.account_id = c.created_by
                    WHERE c.blog_id = @BlogId AND c.id = @id
                    """, new { post.BlogId, id });
                if (c is null || c.Revoked) return "The agent that scheduled it was revoked.";
                if (c.PersonRole is not { } role) return "The person whose agent scheduled it is no longer on this blog.";
                var worksAt = Levels.Lower(c.Level, Levels.CapFor(role));
                if (Levels.Rank(worksAt) >= Levels.Rank(Levels.Publish)) return null;
                return worksAt == c.Level
                    ? $"The agent that scheduled it is now at {c.Level}, which can't publish."
                    : $"The agent that scheduled it works for a {role} now, who can't publish.";
            default:
                return "Nobody vouches for this schedule.";
        }
    }

    private sealed record Due(Guid Id, Guid BlogId, string ScheduledBy, DateTime PublishAt);

    private sealed record ConnectionState(string Level, bool Revoked, string? PersonRole);

    /// <summary>The background loop: every <c>Blog__Scheduler__IntervalSeconds</c> (30; 0 turns it off, as the tests do). It also sweeps expired uploads.</summary>
    public sealed class Worker(Scheduler scheduler, AgentBlog.Media.MediaService media, AgentBlog.Media.CdnLinks cdn, TimeProvider clock, IConfiguration config, ILogger<Worker> log) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var seconds = config.GetValue("Blog:Scheduler:IntervalSeconds", 30);
            if (seconds <= 0) return;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    foreach (var f in await scheduler.RunOnceAsync(clock.GetUtcNow(), stoppingToken))
                        log.LogInformation("Scheduled post {Post} {Outcome}{Reason}", f.PostId, f.Outcome, f.Reason is null ? "" : ": " + f.Reason);
                    // The same pass expires upload slots nobody confirmed and removes their bytes (docs/specs/media.md).
                    if (await media.SweepAsync(clock.GetUtcNow(), stoppingToken) is > 0 and var n)
                        log.LogInformation("Expired {Count} unconfirmed uploads", n);
                    // And purges trashed pictures from Cloudflare's edge, for blogs that connected it (decision 0009).
                    if (await cdn.DrainAsync(stoppingToken) is > 0 and var purged)
                        log.LogInformation("Purged {Count} trashed pictures from Cloudflare's edge", purged);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A failed pass (a database restart) is retried on the next tick; nothing is lost, the rows stay due.
                    log.LogWarning(e, "The scheduler pass failed; it runs again in {Seconds} s.", seconds);
                }
            }
        }
    }
}
