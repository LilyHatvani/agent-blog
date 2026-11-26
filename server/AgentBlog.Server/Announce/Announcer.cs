using System.Text.Json;
using AgentBlog.Auth;
using AgentBlog.Database;
using AgentBlog.Posts;
using AgentBlog.Public;
using AgentKit.Channels;
using AgentKit.Social;

namespace AgentBlog.Announce;

/// <summary>One announcement as the owner and the worker see it.</summary>
public sealed class AnnouncementRow
{
    public Guid Id { get; set; }
    public Guid BlogId { get; set; }
    public Guid PostId { get; set; }
    public Guid AccountId { get; set; }
    public string State { get; set; } = "";
    public string? WhyWaiting { get; set; }
    public string? Text { get; set; }
    public string? TextBy { get; set; }
    public string? TextByLabel { get; set; }
    public DateTime? DueAt { get; set; }
    public int Attempts { get; set; }
    public bool ResendUnconfirmed { get; set; }
    public string? RemoteUrl { get; set; }
    public string? Error { get; set; }
    public string[] Notes { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

/// <summary>What a post and its blog are when an announcement is built, read fresh each time. A class: Dapper can't pass a <c>text[]</c> to a constructor.</summary>
public sealed class PostFacts
{
    public Guid Id { get; set; }
    public Guid BlogId { get; set; }
    public string Slug { get; set; } = "";
    public string Status { get; set; } = "";
    public string Title { get; set; } = "";
    public string Excerpt { get; set; } = "";
    public string BodyMd { get; set; } = "";
    public DateTime? PublishedAt { get; set; }
    public string BlogSlug { get; set; } = "";
    public string BlogTitle { get; set; } = "";
    public string Theme { get; set; } = "";
    public bool Hidden { get; set; }
    public string[] Tags { get; set; } = [];
}

/// <summary>An announcement's text as a network would send it, for the editor and the agent.</summary>
public sealed record Composed(string Text, int Length, int MaxLength, string Unit, IReadOnlyList<string> Problems, bool IsDefault);

/// <summary>
/// Announcing new posts (docs/specs/announce.md). One pass reads the event log and makes an announcement per account
/// for each post's first publication: scheduled after the blog's wait when the owner vouched for the post, waiting for
/// the owner's tap when an agent published it on its own or wrote its text. Another pass sends what is due, judging it
/// at that moment (the post still live, the account still there), through AgentKit.Social with the announcement row as
/// the ledger, so a retry never posts twice where the network can be asked. Every change writes an event.
/// </summary>
public sealed class Announcer(Db db, SocialAccounts accounts, SearchPing urls, Theme theme, TimeProvider clock, ILogger<Announcer> log)
{
    public const int DefaultDelayMinutes = 10;
    public const int MaxDelayMinutes = 24 * 60;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    public static readonly string[] OpenStates = ["draft", "waiting", "scheduled", "failed", "unconfirmed"];

    private const string SelectRow = """
        SELECT a.id, a.blog_id, a.post_id, a.account_id, a.state, a.why_waiting, a.text, a.text_by,
               CASE WHEN a.text_by IS NULL THEN NULL ELSE actor_label(a.text_by) END AS text_by_label, a.due_at, a.attempts,
               a.resend_unconfirmed, a.remote_url, a.error, a.notes, a.created_at, a.updated_at, a.sent_at
        FROM announcement a
        """;

    // ------------------------------------------------------------------------------------------------ making them

    private sealed record Published(long Id, Guid BlogId, Guid PostId, string Actor, string? ScheduledBy, DateTime At);

    /// <summary>
    /// Who vouched for a publication: the owner when an account published it (directly or by approving the request) or
    /// a schedule an account set; otherwise an agent on its own.
    /// </summary>
    public static bool OwnerVouched(string actor, string? scheduledBy) =>
        actor.StartsWith("account:", StringComparison.Ordinal)
        || (actor == Actors.System && scheduledBy is { } s && s.StartsWith("account:", StringComparison.Ordinal));

    /// <summary>One pass over new <c>post_published</c> events. Returns how many announcements it made.</summary>
    public async Task<int> CreatePassAsync(CancellationToken ct)
    {
        var events = await db.OperationalAsync<List<Published>>(async tx =>
        {
            var from = await tx.One<long>("SELECT last_event_id FROM notify_cursor WHERE name = 'announce' FOR UPDATE");
            var page = (await tx.Query<(long Id, string Kind, bool Imported)>(
                "SELECT id, kind, imported FROM event WHERE id > @from ORDER BY id LIMIT 1000", new { from })).ToList();
            if (page.Count == 0) return [];
            // An imported publication was announced (or not) where it happened: never again here.
            var ids = page.Where(e => e.Kind == "post_published" && !e.Imported).Select(e => e.Id).ToArray();
            // Only a post's first publication: an earlier post_published for the same post means it was announced (or
            // could have been) then.
            var found = ids.Length == 0 ? [] : (await tx.Query<Published>("""
                SELECT e.id, e.blog_id, e.target_id AS post_id, e.actor, e.payload->>'scheduled_by' AS scheduled_by, e.at
                FROM event e
                WHERE e.id = ANY (@ids)
                  AND NOT EXISTS (SELECT 1 FROM event f WHERE f.target_type = 'post' AND f.target_id = e.target_id
                                  AND f.kind IN ('post_published', 'post_imported') AND f.id < e.id)
                ORDER BY e.id
                """, new { ids })).ToList();
            await tx.Exec("UPDATE notify_cursor SET last_event_id = @last WHERE name = 'announce'", new { last = page[^1].Id });
            return found;
        }, ct);

        var made = 0;
        foreach (var e in events)
            made += await db.WriteAsync(async tx =>
            {
                var vouched = OwnerVouched(e.Actor, e.ScheduledBy);
                var delay = await tx.One<int?>("SELECT (settings->>'announce_delay_minutes')::int FROM blog WHERE id = @BlogId", e) ?? DefaultDelayMinutes;
                var now = clock.GetUtcNow();
                var due = new DateTimeOffset(DateTime.SpecifyKind(e.At, DateTimeKind.Utc)).AddMinutes(Math.Clamp(delay, 0, MaxDelayMinutes));
                // Accounts that announce new posts and were there when it went live; a draft the owner or an agent wrote
                // for any account (auto or not) turns into an announcement too.
                var targets = await tx.Query<(Guid AccountId, Guid? AnnouncementId, string? TextBy)>("""
                    SELECT s.id, a.id, a.text_by
                    FROM social_account s
                    LEFT JOIN announcement a ON a.account_id = s.id AND a.post_id = @PostId
                    WHERE s.blog_id = @BlogId AND s.removed_at IS NULL AND s.enabled
                      AND ((s.auto AND s.created_at <= @At AND a.id IS NULL) OR a.state = 'draft')
                    """, e);
                var count = 0;
                foreach (var (accountId, existing, textBy) in targets)
                {
                    var agentText = textBy is not null && !textBy.StartsWith("account:", StringComparison.Ordinal);
                    var (state, why) = !vouched ? ("waiting", "agent_published") : agentText ? ("waiting", "agent_text") : ("scheduled", (string?)null);
                    var id = existing ?? Guid.CreateVersion7();
                    if (existing is null)
                        await tx.Exec("""
                            INSERT INTO announcement (id, blog_id, post_id, account_id, state, why_waiting, due_at, created_at, updated_at)
                            VALUES (@id, @BlogId, @PostId, @accountId, @state, @why, @due, @now, @now)
                            """, new { id, e.BlogId, e.PostId, accountId, state, why, due = state == "scheduled" ? due : (DateTimeOffset?)null, now });
                    else
                        await tx.Exec("""
                            UPDATE announcement SET state = @state, why_waiting = @why, due_at = @due, updated_at = @now WHERE id = @id
                            """, new { id, state, why, due = state == "scheduled" ? due : (DateTimeOffset?)null, now });
                    await tx.Event(new EventRow(e.BlogId, "announcement", id, state == "waiting" ? "announcement_waiting" : "announcement_scheduled",
                        Actors.System, new { post = e.PostId, account = accountId, why, due = state == "scheduled" ? due : (DateTimeOffset?)null }), now);
                    count++;
                }
                return count;
            }, ct);
        return made;
    }

    // ------------------------------------------------------------------------------------------------ what it says

    public Task<PostFacts?> PostAsync(Guid blogId, Guid postId, CancellationToken ct) => db.ReadAsync(tx => tx.One<PostFacts>("""
        SELECT p.id, p.blog_id, p.slug, p.status, r.title, r.excerpt, r.body_md, p.published_at, b.slug AS blog_slug, b.title AS blog_title,
               coalesce(b.settings->>'theme', '') AS theme, b.hidden_at IS NOT NULL AS hidden,
               ARRAY(SELECT t.name FROM term t WHERE t.id = ANY (r.term_ids) AND t.kind = 'tag' ORDER BY lower(t.name)) AS tags
        FROM post p JOIN post_revision r ON r.id = p.current_revision_id JOIN blog b ON b.id = p.blog_id
        WHERE p.blog_id = @blogId AND p.id = @postId
        """, new { blogId, postId }), ct);

    /// <summary>The post's public address, or null when this server doesn't know its own.</summary>
    public string? LinkOf(PostFacts p) => urls.UrlOf(p.BlogSlug) is { } blog ? $"{blog}posts/{Uri.EscapeDataString(p.Slug)}" : null;

    /// <summary>
    /// The post as <paramref name="channel"/> would send it: <paramref name="text"/> when someone wrote it, else the
    /// default (title, excerpt, tags), dropping the tags and then the excerpt until it fits.
    /// </summary>
    public async Task<(SocialPost Post, Composed Composed)> BuildAsync(ISocialChannel channel, PostFacts p, string? text, Guid announcementId, CancellationToken ct)
    {
        var url = LinkOf(p);
        var link = url is null ? null : new Uri(url);
        var published = p.PublishedAt ?? clock.GetUtcNow().UtcDateTime;
        var excerpt = p.Excerpt.Length > 0 ? p.Excerpt : Markdown.ToPlainText(p.BodyMd, 200);
        LinkCard? card = null;
        if (link is not null && urls.UrlOf(p.BlogSlug) is { } blogUrl)
        {
            var image = await Social.PostImageAsync(db, p.BlogId, p.BlogTitle, theme.Exists(p.Theme) ? p.Theme : Theme.Default, blogUrl, p.Slug,
                p.Title, p.BodyMd, published, ct);
            card = new LinkCard
            {
                Title = p.Title,
                Description = excerpt,
                ImageUrl = new Uri(image.Url),
                ImageAlt = image.Alt.Length > 0 ? image.Alt : p.Title,
                Footer = p.BlogTitle,
                PublishedAt = new DateTimeOffset(DateTime.SpecifyKind(published, DateTimeKind.Utc)),
            };
        }
        var key = $"announcement:{announcementId:D}";
        SocialPost Make(string words, IReadOnlyList<string> tags) =>
            new() { Text = words, Link = link, Card = card, Tags = tags, IdempotencyKey = key };

        if (text is not null)
        {
            var own = Make(text, []);
            return (own, From(channel.Compose(own), false));
        }
        if (channel.Network == "email")
        {
            // The email's heading is the title already: its default words are the excerpt, else the post's opening.
            var email = Make(excerpt.Length > 0 ? excerpt : p.Title, []);
            return (email, From(channel.Compose(email), true));
        }
        var withExcerpt = p.Excerpt.Length > 0 ? $"{p.Title}\n\n{p.Excerpt}" : p.Title;
        SocialPost? last = null;
        foreach (var candidate in new[] { Make(withExcerpt, p.Tags), Make(withExcerpt, []), Make(p.Title, []) })
        {
            last = candidate;
            var c = channel.Compose(candidate);
            if (c.Fits) return (candidate, From(c, true));
        }
        return (last!, From(channel.Compose(last!), true));
    }

    private static Composed From(SocialComposition c, bool isDefault) => new(c.Text, c.Length, c.MaxLength, c.Unit, c.Problems, isDefault);

    // ------------------------------------------------------------------------------------------------ sending them

    /// <summary>One pass over what is due at <paramref name="now"/>. Returns the ids it took.</summary>
    public async Task<IReadOnlyList<Guid>> SendPassAsync(DateTimeOffset now, CancellationToken ct)
    {
        // A lease, so two replicas (or a slow send) don't take the same row; a crashed send comes round again after it,
        // and the ledger then resumes its attempt rather than starting a new one.
        var due = await db.OperationalAsync(async tx => (await tx.Query<Guid>("""
            UPDATE announcement SET lease_until = @until
            WHERE id IN (SELECT id FROM announcement
                         WHERE state = 'scheduled' AND due_at <= @now AND (lease_until IS NULL OR lease_until < @now)
                         ORDER BY due_at LIMIT 20 FOR UPDATE SKIP LOCKED)
            RETURNING id
            """, new { now, until = now + Lease })).ToList(), ct);
        foreach (var id in due)
        {
            try { await SendOneAsync(id, ct); }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning(e, "Announcement {Id} failed to send; it comes round again after its lease.", id);
            }
        }
        return due;
    }

    private sealed record Sending(Guid Id, Guid BlogId, Guid PostId, Guid AccountId, string State, string? Text, int Attempts, bool ResendUnconfirmed,
        bool AccountRemoved, bool AccountEnabled);

    private async Task SendOneAsync(Guid id, CancellationToken ct)
    {
        var a = await db.ReadAsync(tx => tx.One<Sending>("""
            SELECT a.id, a.blog_id, a.post_id, a.account_id, a.state, a.text, a.attempts, a.resend_unconfirmed,
                   s.removed_at IS NOT NULL AS account_removed, s.enabled AS account_enabled
            FROM announcement a JOIN social_account s ON s.id = a.account_id WHERE a.id = @id
            """, new { id }), ct);
        if (a is null || a.State != "scheduled") return;

        // Judged now, not when it was made.
        if (a.AccountRemoved) { await EndAsync(a, "cancelled", "The account was removed.", ct); return; }
        if (!a.AccountEnabled) { await EndAsync(a, "skipped", "The account is turned off.", ct); return; }
        var post = await PostAsync(a.BlogId, a.PostId, ct);
        if (post is null || post.Status != "live") { await EndAsync(a, "skipped", "The post isn't live any more.", ct); return; }
        if (post.Hidden) { await EndAsync(a, "skipped", "The blog is hidden.", ct); return; }
        if (LinkOf(post) is null)
        {
            await EndAsync(a, "failed", "This server doesn't know its public address: set Blog__PublicUrl (or a hosting domain).", ct);
            return;
        }
        var account = await accounts.GetAsync(a.BlogId, a.AccountId, ct);
        var channel = account is null ? null : await accounts.ChannelAsync(account, ct);
        if (channel is null)
        {
            await EndAsync(a, "failed", "The account's credentials don't open: remove it and add it again.", ct, accountError: true);
            return;
        }

        var (socialPost, composed) = await BuildAsync(channel, post, a.Text, a.Id, ct);
        if (composed.Problems.Count > 0)
        {
            await EndAsync(a, "failed", Cut(string.Join(" ", composed.Problems)), ct);
            return;
        }
        SocialPostResult result;
        try
        {
            result = await new SocialPublisher(new AnnouncementLedger(db)).PostAsync(channel, socialPost, a.ResendUnconfirmed, ct);
        }
        catch (InvalidOperationException e)
        {
            // The ledger holds an attempt for another channel id: the account's network identity changed under it.
            await EndAsync(a, "failed", Cut(e.Message), ct);
            return;
        }
        await RecordAsync(a, result, ct);
    }

    private Task RecordAsync(Sending a, SocialPostResult result, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.WriteAsync(async tx =>
        {
            if (result.Posted)
            {
                await tx.Exec("""
                    UPDATE announcement SET state = 'posted', remote_url = @url, error = NULL, notes = @notes, sent_at = @now, updated_at = @now,
                        lease_until = NULL, resend_unconfirmed = false
                    WHERE id = @Id
                    """, new { a.Id, url = result.PostUrl?.AbsoluteUri, notes = result.Notes.Select(Cut).ToArray(), now });
                await tx.Exec("UPDATE social_account SET last_error = NULL, failing_since = NULL WHERE id = @AccountId", a);
                await tx.Event(new EventRow(a.BlogId, "announcement", a.Id, "announcement_posted", Actors.System,
                    new { post = a.PostId, account = a.AccountId, url = result.PostUrl?.AbsoluteUri }), now);
                return 0;
            }
            var f = result.Failure!;
            var message = Cut(f.Message);
            switch (f.Kind)
            {
                case SocialFailureKind.RateLimited or SocialFailureKind.Transient:
                    var attempts = a.Attempts + 1;
                    if (Retries.Next(attempts, f.RetryAfter) is { } wait)
                    {
                        await tx.Exec("""
                            UPDATE announcement SET attempts = @attempts, due_at = @due, error = @message, updated_at = @now, lease_until = NULL
                            WHERE id = @Id
                            """, new { a.Id, attempts, due = now + wait, message, now });
                        await tx.Event(new EventRow(a.BlogId, "announcement", a.Id, "announcement_retrying", Actors.System,
                            new { attempts, after_seconds = (int)wait.TotalSeconds, reason = message }), now);
                        return 0;
                    }
                    await Fail(tx, a, "failed", $"Gave up after {attempts} tries: {message}", now, accountError: false);
                    return 0;
                case SocialFailureKind.Unconfirmed:
                    await Fail(tx, a, "unconfirmed", message, now, accountError: false);
                    return 0;
                default:
                    await Fail(tx, a, "failed", message, now, accountError: f.Kind == SocialFailureKind.Auth);
                    return 0;
            }
        }, ct);
    }

    private Task EndAsync(Sending a, string state, string reason, CancellationToken ct, bool accountError = false)
    {
        var now = clock.GetUtcNow();
        return db.WriteAsync(async tx => { await Fail(tx, a, state, reason, now, accountError); return 0; }, ct);
    }

    private static async Task Fail(Tx tx, Sending a, string state, string reason, DateTimeOffset now, bool accountError)
    {
        await tx.Exec("""
            UPDATE announcement SET state = @state, error = @reason, updated_at = @now, lease_until = NULL, resend_unconfirmed = false WHERE id = @Id
            """, new { a.Id, state, reason, now });
        if (accountError)
            await tx.Exec("UPDATE social_account SET last_error = @reason, failing_since = coalesce(failing_since, @now) WHERE id = @AccountId",
                new { a.AccountId, reason, now });
        var kind = state switch
        {
            "unconfirmed" => "announcement_unconfirmed",
            "skipped" => "announcement_skipped",
            "cancelled" => "announcement_cancelled",
            _ => "announcement_failed",
        };
        await tx.Event(new EventRow(a.BlogId, "announcement", a.Id, kind, Actors.System, new { post = a.PostId, account = a.AccountId, reason }), now);
    }

    private static string Cut(string s) => s.Length <= 500 ? s : XmlText.Cut(s, 499) + "…";

    // ------------------------------------------------------------------------------------------------ reading them

    public Task<IReadOnlyList<AnnouncementRow>> ForPostAsync(Guid blogId, Guid postId, CancellationToken ct) => db.ReadAsync(async tx =>
        (IReadOnlyList<AnnouncementRow>)[.. await tx.Query<AnnouncementRow>($"{SelectRow} WHERE a.blog_id = @blogId AND a.post_id = @postId",
            new { blogId, postId })], ct);

    /// <summary>Where each of these posts has been announced: one row per posted announcement, its network and address there.</summary>
    public Task<IReadOnlyList<PostedRow>> PostedForAsync(Guid blogId, IReadOnlyCollection<Guid> postIds, CancellationToken ct) =>
        postIds.Count == 0 ? Task.FromResult<IReadOnlyList<PostedRow>>([]) : db.ReadAsync(async tx =>
        (IReadOnlyList<PostedRow>)[.. await tx.Query<PostedRow>("""
            SELECT a.post_id, s.network, a.remote_url FROM announcement a
            JOIN social_account s ON s.blog_id = a.blog_id AND s.id = a.account_id
            WHERE a.blog_id = @blogId AND a.post_id = ANY(@ids) AND a.state = 'posted'
            ORDER BY a.sent_at NULLS LAST, s.network
            """, new { blogId, ids = postIds.ToArray() })], ct);

    public sealed record PostedRow(Guid PostId, string Network, string? RemoteUrl);

    public Task<IReadOnlyList<AnnouncementRow>> WaitingAsync(Guid blogId, CancellationToken ct) => db.ReadAsync(async tx =>
        (IReadOnlyList<AnnouncementRow>)[.. await tx.Query<AnnouncementRow>(
            $"{SelectRow} WHERE a.blog_id = @blogId AND a.state IN ('waiting', 'unconfirmed', 'failed') ORDER BY a.updated_at DESC LIMIT 50",
            new { blogId })], ct);

    // ------------------------------------------------------------------------------------------------ the owner's and the agent's hands

    /// <summary>
    /// Sets the text of a post's announcement to <paramref name="text"/> (null: back to the default), writing the row as a
    /// draft when there is none yet. Text an agent writes makes a scheduled announcement wait for the owner's tap.
    /// Refused, with the network's reasons, when it doesn't fit; refused when the announcement is past changing; a
    /// writer (or their agent) on someone else's post is refused, and nothing changes.
    /// </summary>
    public async Task<(AnnouncementRow? Row, Composed? Composed, string? Error)> WriteAsync(Actor actor, Guid postId, Guid accountId, string? text, CancellationToken ct)
    {
        text = text?.Trim();
        if (text is { Length: 0 }) text = null;
        if (text is { Length: > 5000 }) return (null, null, "An announcement is at most 5000 characters before the network's own limit.");
        if (!actor.IsOwner && text is null) return (null, null, "Write the text: only the owner sets an announcement back to the default.");
        var post = await PostAsync(actor.BlogId, postId, ct);
        if (post is null || post.Status == "trash") return (null, null, "No such post.");
        var account = await accounts.GetAsync(actor.BlogId, accountId, ct);
        if (account is null) return (null, null, "No such account.");
        var channel = await accounts.ChannelAsync(account, ct);
        if (channel is null) return (null, null, "The account's credentials don't open: the owner sets it up again.");
        var existing = (await ForPostAsync(actor.BlogId, postId, ct)).FirstOrDefault(r => r.AccountId == accountId);
        if (existing is { State: "posted" or "skipped" or "cancelled" or "unconfirmed" })
            return (null, null, $"That announcement is {existing.State}: its text can't change now.");
        var id = existing?.Id ?? Guid.CreateVersion7();
        var (_, composed) = await BuildAsync(channel, post, text, id, ct);
        if (text is not null && !composed.Fits()) return (null, composed, string.Join(" ", composed.Problems));

        var now = clock.GetUtcNow();
        var refusal = await db.WriteAsync<string?>(async tx =>
        {
            // A writer, and a writer's agent, write announcements only on the writer's own posts
            // (docs/specs/members.md), the same rule as every other post change.
            if (actor.Role == Roles.Writer && actor.AccountId is { } personId && !await tx.One<bool>(PostService.IsOwnPostSql, new { postId, personId }))
                return actor.IsPerson
                    ? "A writer changes only their own posts; this one is someone else's."
                    : "You're a writer's agent: you change only your writer's posts (theirs or ones you started), and this one is someone else's.";
            if (existing is null)
                await tx.Exec("""
                    INSERT INTO announcement (id, blog_id, post_id, account_id, state, text, text_by, created_at, updated_at)
                    VALUES (@id, @BlogId, @postId, @accountId, 'draft', @text, @by, @now, @now)
                    ON CONFLICT (post_id, account_id) DO NOTHING
                    """, new { id, actor.BlogId, postId, accountId, text, by = text is null ? null : actor.Value, now });
            else
                // An agent's words on a scheduled announcement stop it: the owner taps to send them.
                await tx.Exec("""
                    UPDATE announcement SET text = @text, text_by = @by, updated_at = @now,
                        state = CASE WHEN @agent AND state = 'scheduled' THEN 'waiting' ELSE state END,
                        why_waiting = CASE WHEN @agent AND state = 'scheduled' THEN 'agent_text' ELSE why_waiting END,
                        due_at = CASE WHEN @agent AND state = 'scheduled' THEN NULL ELSE due_at END
                    WHERE id = @id AND state IN ('draft', 'waiting', 'scheduled', 'failed')
                    """, new { id, text, by = text is null ? null : actor.Value, agent = !actor.IsOwner, now });
            await tx.Event(new EventRow(actor.BlogId, "announcement", id, "announcement_written", actor.Value,
                new { post = postId, account = accountId, @default = text is null }), now);
            return null;
        }, ct);
        if (refusal is not null) return (null, composed, refusal);
        var row = (await ForPostAsync(actor.BlogId, postId, ct)).First(r => r.AccountId == accountId);
        return (row, composed, null);
    }

    /// <summary>
    /// The owner's buttons (docs/specs/announce.md): <c>send</c> (now, from any state but posted or unconfirmed; a post
    /// that isn't announced yet gets its row), <c>resend</c> (an unconfirmed one, after looking), <c>done</c> (an
    /// unconfirmed one that is there after all), <c>cancel</c>. Returns why not, or null.
    /// </summary>
    public async Task<string?> ActAsync(Actor owner, Guid postId, Guid accountId, string action, CancellationToken ct)
    {
        if (!owner.IsOwner) return "Only the owner sends announcements.";
        var post = await PostAsync(owner.BlogId, postId, ct);
        if (post is null) return "No such post.";
        var account = await accounts.GetAsync(owner.BlogId, accountId, ct);
        if (account is null) return "No such account.";
        var now = clock.GetUtcNow();
        return await db.WriteAsync<string?>(async tx =>
        {
            var found = await tx.One<RowState>(
                "SELECT id, state FROM announcement WHERE post_id = @postId AND account_id = @accountId FOR UPDATE", new { postId, accountId });
            var row = found is null ? ((Guid Id, string State)?)null : (found.Id, found.State);
            string kind;
            switch (action)
            {
                case "send":
                    if (post.Status != "live") return "The post isn't live: publish it first.";
                    if (!account.Enabled) return "The account is turned off.";
                    if (row is { State: "posted" }) return "It's posted already.";
                    if (row is { State: "unconfirmed" }) return "Look on the network first, then Send again or It's there.";
                    if (row is null)
                    {
                        row = (Guid.CreateVersion7(), "none");
                        await tx.Exec("""
                            INSERT INTO announcement (id, blog_id, post_id, account_id, state, due_at, created_at, updated_at)
                            VALUES (@id, @BlogId, @postId, @accountId, 'scheduled', @now, @now, @now)
                            """, new { id = row.Value.Id, owner.BlogId, postId, accountId, now });
                    }
                    else
                        await tx.Exec("""
                            UPDATE announcement SET state = 'scheduled', due_at = @now, attempts = 0, error = NULL, why_waiting = NULL,
                                lease_until = NULL, updated_at = @now
                            WHERE id = @Id
                            """, new { row.Value.Id, now });
                    kind = "announcement_sent_by_owner";
                    break;
                case "resend":
                    if (row is not { State: "unconfirmed" }) return "Only an unconfirmed announcement is sent again.";
                    if (post.Status != "live") return "The post isn't live: publish it first.";
                    await tx.Exec("""
                        UPDATE announcement SET state = 'scheduled', due_at = @now, resend_unconfirmed = true, attempts = 0, error = NULL,
                            lease_until = NULL, updated_at = @now
                        WHERE id = @Id
                        """, new { row.Value.Id, now });
                    kind = "announcement_resent";
                    break;
                case "done":
                    if (row is not { State: "unconfirmed" }) return "Only an unconfirmed announcement is marked as there.";
                    await tx.Exec("""
                        UPDATE announcement SET state = 'posted', attempt_state = 'posted', error = NULL, sent_at = coalesce(sent_at, @now),
                            notes = array_append(notes, 'Marked as posted by the owner.'), updated_at = @now
                        WHERE id = @Id
                        """, new { row.Value.Id, now });
                    kind = "announcement_marked_posted";
                    break;
                case "cancel":
                    if (row is not { State: "draft" or "waiting" or "scheduled" or "failed" }) return "There is nothing waiting to cancel.";
                    await tx.Exec("UPDATE announcement SET state = 'cancelled', lease_until = NULL, updated_at = @now WHERE id = @Id", new { row.Value.Id, now });
                    kind = "announcement_cancelled";
                    break;
                default:
                    return "send, resend, done or cancel.";
            }
            await tx.Event(new EventRow(owner.BlogId, "announcement", row!.Value.Id, kind, owner.Value, new { post = postId, account = accountId }), now);
            return null;
        }, ct);
    }

    private sealed record RowState(Guid Id, string State);

    /// <summary>The blog's wait before an announcement goes, in minutes.</summary>
    public Task<int> DelayAsync(Guid blogId, CancellationToken ct) => db.ReadAsync(async tx =>
        await tx.One<int?>("SELECT (settings->>'announce_delay_minutes')::int FROM blog WHERE id = @blogId", new { blogId }) ?? DefaultDelayMinutes, ct);

    public async Task<string?> SetDelayAsync(Actor owner, int minutes, CancellationToken ct)
    {
        if (minutes is < 0 or > MaxDelayMinutes) return $"The wait is 0 to {MaxDelayMinutes} minutes.";
        var now = clock.GetUtcNow();
        await db.WriteAsync(async tx =>
        {
            await tx.Exec("UPDATE blog SET settings = settings || jsonb_build_object('announce_delay_minutes', @minutes) WHERE id = @BlogId",
                new { owner.BlogId, minutes });
            await tx.Event(new EventRow(owner.BlogId, "blog", owner.BlogId, "blog_settings_changed", owner.Value, new { announce_delay_minutes = minutes }), now);
            return 0;
        }, ct);
        return null;
    }

    /// <summary>The background loop: every <c>Blog__Announce__IntervalSeconds</c> (15; 0 turns it off, as the tests do).</summary>
    public sealed class Worker(Announcer announcer, IConfiguration config, TimeProvider clock, ILogger<Worker> log) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var seconds = config.GetValue("Blog:Announce:IntervalSeconds", 15);
            if (seconds <= 0) return;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await announcer.CreatePassAsync(stoppingToken);
                    await announcer.SendPassAsync(clock.GetUtcNow(), stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning(e, "The announce pass failed; it runs again in {Seconds} s.", seconds);
                }
            }
        }
    }
}

public static class ComposedExtensions
{
    public static bool Fits(this Composed c) => c.Problems.Count == 0;
}

/// <summary>
/// AgentKit.Social's ledger on the announcement row (docs/specs/announce.md): the attempt the publisher stores before
/// sending, so a retry after a lost answer resumes it. The key is <c>announcement:&lt;id&gt;</c>.
/// </summary>
public sealed class AnnouncementLedger(Db db) : ISocialPostLedger
{
    private sealed record Stored(string? AttemptToken, DateTime? AttemptStartedAt, string? AttemptState, string? AttemptChannel, string? RemoteId, string? RemoteUrl);

    private static Guid IdOf(string key) =>
        key.StartsWith("announcement:", StringComparison.Ordinal) && Guid.TryParse(key["announcement:".Length..], out var id)
            ? id : throw new ArgumentException($"Not an announcement key: {key}.", nameof(key));

    public Task<SocialAttemptRecord> BeginAsync(SocialAttemptRecord attempt, CancellationToken cancellationToken) => db.OperationalAsync(async tx =>
    {
        var id = IdOf(attempt.IdempotencyKey);
        // Atomic: only a row with no attempt or a failed one takes the new attempt; otherwise the earlier one stands.
        var stored = await tx.One<Stored>("""
            UPDATE announcement SET attempt_token = @Token, attempt_started_at = @StartedAt, attempt_state = 'pending', attempt_channel = @ChannelId,
                remote_id = NULL
            WHERE id = @id AND (attempt_state IS NULL OR attempt_state = 'failed')
            RETURNING attempt_token, attempt_started_at, attempt_state, attempt_channel, remote_id, remote_url
            """, new { id, attempt.Token, attempt.StartedAt, attempt.ChannelId })
            ?? await tx.One<Stored>(
                "SELECT attempt_token, attempt_started_at, attempt_state, attempt_channel, remote_id, remote_url FROM announcement WHERE id = @id", new { id })
            ?? throw new InvalidOperationException($"No announcement {id}.");
        return new SocialAttemptRecord
        {
            IdempotencyKey = attempt.IdempotencyKey,
            ChannelId = stored.AttemptChannel ?? attempt.ChannelId,
            Token = stored.AttemptToken!,
            StartedAt = new DateTimeOffset(DateTime.SpecifyKind(stored.AttemptStartedAt!.Value, DateTimeKind.Utc)),
            State = stored.AttemptState switch { "posted" => SocialAttemptState.Posted, "failed" => SocialAttemptState.Failed, _ => SocialAttemptState.Pending },
            PostId = stored.RemoteId,
            PostUrl = stored.RemoteUrl is { } u ? new Uri(u) : null,
        };
    }, cancellationToken);

    public Task<bool> SaveAsync(SocialAttemptRecord record, CancellationToken cancellationToken) => db.OperationalAsync(async tx =>
        await tx.Exec("""
            UPDATE announcement SET attempt_state = @state, remote_id = coalesce(@PostId, remote_id), remote_url = coalesce(@url, remote_url)
            WHERE id = @id AND attempt_token = @Token AND attempt_state = 'pending'
            """, new
        {
            id = IdOf(record.IdempotencyKey),
            state = record.State switch { SocialAttemptState.Posted => "posted", SocialAttemptState.Failed => "failed", _ => "pending" },
            record.PostId,
            url = record.PostUrl?.AbsoluteUri,
            record.Token,
        }) == 1, cancellationToken);
}
