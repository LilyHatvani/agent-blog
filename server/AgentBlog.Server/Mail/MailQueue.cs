using System.Text.Encodings.Web;
using AgentBlog.Announce;
using AgentBlog.Database;
using AgentBlog.Posts;
using AgentBlog.Public;
using AgentKit.Channels;
using AgentKit.Social;

namespace AgentBlog.Mail;

/// <summary>
/// Outgoing mail (docs/specs/subscribe.md): confirmations and new-post emails, one row per subscriber, sent by a worker
/// through the blog's Email account and judged when sent: the post still live, the reader still subscribed, the
/// account still there. Each row has its own retries; a refused credential holds the whole queue for an hour.
/// </summary>
public sealed class MailQueue(Db db, Subscribers subscribers, SocialAccounts accounts, Bylines bylines, SearchPing urls,
    IHttpClientFactory http, ILogger<MailQueue> log)
{
    public const string HttpClientName = "blog-mail";
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AuthHold = TimeSpan.FromHours(1);

    /// <summary>
    /// The new-post email for every confirmed subscriber of the announcement's blog: one row each, once (a retried
    /// announcement adds nobody twice), and the text it said kept on the announcement. Returns how many are queued.
    /// </summary>
    public static Task<int> EnqueuePostAsync(Db db, Guid announcementId, string text, DateTimeOffset now, CancellationToken ct) =>
        db.OperationalAsync(async tx =>
        {
            await tx.Exec("UPDATE announcement SET sent_text = @text WHERE id = @announcementId", new { announcementId, text });
            await tx.Exec("""
                INSERT INTO mail_out (id, blog_id, subscriber_id, kind, announcement_id, next_at, created_at)
                SELECT gen_random_uuid(), s.blog_id, s.id, 'post', a.id, @now, @now
                FROM announcement a JOIN subscriber s ON s.blog_id = a.blog_id AND s.state = 'confirmed'
                WHERE a.id = @announcementId
                ON CONFLICT (announcement_id, subscriber_id) WHERE announcement_id IS NOT NULL DO NOTHING
                """, new { announcementId, now });
            return await tx.One<int>("SELECT count(*)::int FROM mail_out WHERE announcement_id = @announcementId", new { announcementId });
        }, ct);

    private sealed record Due(Guid Id, Guid BlogId, Guid SubscriberId, string Kind, Guid? AnnouncementId, int Attempts);

    /// <summary>One pass over what is due at <paramref name="now"/>. Returns how many it sent.</summary>
    public async Task<int> SendPassAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = (await db.OperationalAsync(tx => tx.Query<Due>("""
            UPDATE mail_out SET lease_until = @until
            WHERE id IN (SELECT id FROM mail_out WHERE status = 'pending' AND next_at <= @now AND (lease_until IS NULL OR lease_until < @now)
                         ORDER BY next_at LIMIT 50 FOR UPDATE SKIP LOCKED)
            RETURNING id, blog_id, subscriber_id, kind, announcement_id, attempts
            """, new { now, until = now + Lease }), ct)).ToList();
        var sent = 0;
        foreach (var group in due.GroupBy(d => d.BlogId))
        {
            var account = (await accounts.ListAsync(group.Key, ct)).FirstOrDefault(a => a.Network == "email" && a.Enabled);
            var sender = account is null ? null : await SenderAsync(account, ct);
            foreach (var mail in group)
            {
                try
                {
                    if (account is null) { await EndAsync(mail, "skipped", "The blog has no Email account that is on.", ct); continue; }
                    if (sender is null) { await EndAsync(mail, "failed", "The Email account's credentials don't open: remove it and add it again.", ct); continue; }
                    var outcome = await SendOneAsync(mail, account, sender, now, ct);
                    if (outcome == MailOutcome.Sent) sent++;
                    if (outcome == MailOutcome.Auth) break; // the rest of this blog's queue waits with it
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    log.LogWarning(e, "Mail {Id} failed to send; it comes round again after its lease.", mail.Id);
                }
            }
        }
        return sent;
    }

    /// <summary>The account's provider, from its opened credentials; null when they don't open.</summary>
    public async Task<IMailSender?> SenderAsync(SocialAccountRow account, CancellationToken ct)
    {
        if (await accounts.SecretsAsync(account, ct) is not { } s) return null;
        return account.Setting("provider") switch
        {
            "smtp" when s.TryGetValue("host", out var host) && int.TryParse(account.Setting("port"), out var port) =>
                new SmtpSender(host, port, s.GetValueOrDefault("user", ""), s.GetValueOrDefault("password", "")),
            "resend" when s.TryGetValue("api_key", out var key) => new ResendSender(http.CreateClient(HttpClientName), key),
            _ => null,
        };
    }

    private sealed record PostFacts(Guid PostId, string Status, string Title, string Slug, string? SentText, string BlogSlug, string BlogTitle);

    private async Task<MailOutcome?> SendOneAsync(Due mail, SocialAccountRow account, IMailSender sender, DateTimeOffset now, CancellationToken ct)
    {
        if (await subscribers.AddressAsync(mail.BlogId, mail.SubscriberId, ct) is not { } who || who.Email is null)
        {
            await EndAsync(mail, "skipped", "Unsubscribed.", ct);
            return null;
        }
        if (await subscribers.KeyAsync(mail.BlogId, ct) is not { } key)
        {
            await EndAsync(mail, "failed", "The blog's subscriber key doesn't open (the master key changed?).", ct);
            return null;
        }
        var blog = await db.ReadAsync(tx => tx.One<(string Slug, string Title)>("SELECT slug, title FROM blog WHERE id = @BlogId", mail), ct);
        if (urls.UrlOf(blog.Slug) is not { } blogUrl)
        {
            await EndAsync(mail, "failed", "This server doesn't know its public address: set Blog__PublicUrl.", ct);
            return null;
        }
        var unsubscribe = $"{blogUrl}unsubscribe?s={mail.SubscriberId:D}&t={Subscribers.Token(key, "unsubscribe", mail.SubscriberId)}";
        var from = (Address: account.Setting("from") ?? "", Name: account.Setting("from_name") ?? blog.Title);
        Mail message;
        if (mail.Kind == "confirm")
        {
            if (who.State != "pending" || await subscribers.ConfirmExpiresAsync(mail.SubscriberId, ct) is not { } expires
                || expires <= now.UtcDateTime)
            {
                await EndAsync(mail, "skipped", "Confirmed, unsubscribed or expired before it went out.", ct);
                return null;
            }
            var confirm = $"{blogUrl}subscribe/confirm?s={mail.SubscriberId:D}&t={Subscribers.Token(key, "confirm", mail.SubscriberId, expires)}";
            message = MailTemplates.Confirm(from.Address, from.Name, who.Email, blog.Title, blogUrl, confirm);
        }
        else
        {
            var p = await db.ReadAsync(tx => tx.One<PostFacts>("""
                SELECT p.id AS post_id, p.status, r.title, p.slug, a.sent_text, b.slug AS blog_slug, b.title AS blog_title
                FROM announcement a JOIN post p ON p.id = a.post_id JOIN post_revision r ON r.id = p.current_revision_id JOIN blog b ON b.id = p.blog_id
                WHERE a.id = @AnnouncementId
                """, mail), ct);
            if (who.State != "confirmed") { await EndAsync(mail, "skipped", "Not subscribed any more.", ct); return null; }
            if (p is null || p.Status != "live") { await EndAsync(mail, "skipped", "The post isn't live any more.", ct); return null; }
            var byline = await bylines.OnAsync(mail.BlogId, ct) ? (await bylines.ForPostAsync(mail.BlogId, p.PostId, ct))?.Text : null;
            message = MailTemplates.Post(from.Address, from.Name, who.Email, blog.Title, blogUrl, p.Title, $"{blogUrl}posts/{Uri.EscapeDataString(p.Slug)}",
                p.SentText ?? p.Title, byline, unsubscribe);
        }
        var result = await sender.SendAsync(message, $"agentblog-mail-{mail.Id:N}", ct);
        await RecordAsync(mail, account, result, now, ct);
        return result.Outcome;
    }

    private Task RecordAsync(Due mail, SocialAccountRow account, MailResult result, DateTimeOffset now, CancellationToken ct) => db.OperationalAsync(async tx =>
    {
        switch (result.Outcome)
        {
            case MailOutcome.Sent:
                await tx.Exec("UPDATE mail_out SET status = 'sent', sent_at = @now, error = NULL, lease_until = NULL WHERE id = @Id", new { mail.Id, now });
                await tx.Exec("UPDATE social_account SET last_error = NULL, failing_since = NULL WHERE id = @Id AND failing_since IS NOT NULL", account);
                break;
            case MailOutcome.Retry:
                var attempts = mail.Attempts + 1;
                if (Retries.Next(attempts, result.RetryAfter) is { } wait)
                    await tx.Exec("UPDATE mail_out SET attempts = @attempts, next_at = @next, error = @Message, lease_until = NULL WHERE id = @Id",
                        new { mail.Id, attempts, next = now + wait, result.Message });
                else
                    await tx.Exec("UPDATE mail_out SET status = 'failed', attempts = @attempts, error = @Message, lease_until = NULL WHERE id = @Id",
                        new { mail.Id, attempts, result.Message });
                break;
            case MailOutcome.Auth:
                // The account's credentials: everything this blog has waiting holds for an hour, and the account says why.
                await tx.Exec("UPDATE mail_out SET next_at = greatest(next_at, @hold), error = @Message, lease_until = NULL WHERE blog_id = @BlogId AND status = 'pending'",
                    new { mail.BlogId, hold = now + AuthHold, result.Message });
                await tx.Exec("UPDATE social_account SET last_error = @Message, failing_since = coalesce(failing_since, @now) WHERE id = @Id",
                    new { account.Id, result.Message, now });
                break;
            default:
                await tx.Exec("UPDATE mail_out SET status = 'failed', error = @Message, lease_until = NULL WHERE id = @Id", new { mail.Id, result.Message });
                break;
        }
        return 0;
    }, ct);

    private Task EndAsync(Due mail, string status, string reason, CancellationToken ct) => db.OperationalAsync(tx =>
        tx.Exec("UPDATE mail_out SET status = @status, error = @reason, lease_until = NULL WHERE id = @Id", new { mail.Id, status, reason }), ct);

    public sealed record Progress(int Queued, int Sent, int Waiting, int Failed, int Skipped);

    /// <summary>How an announcement's emails are going, for the post's Announce section.</summary>
    public Task<Progress> ProgressAsync(Guid announcementId, CancellationToken ct) => db.ReadAsync(async tx =>
        await tx.One<Progress>("""
            SELECT count(*)::int AS queued, count(*) FILTER (WHERE status = 'sent')::int AS sent, count(*) FILTER (WHERE status = 'pending')::int AS waiting,
                   count(*) FILTER (WHERE status = 'failed')::int AS failed, count(*) FILTER (WHERE status = 'skipped')::int AS skipped
            FROM mail_out WHERE announcement_id = @announcementId
            """, new { announcementId }) ?? new Progress(0, 0, 0, 0, 0), ct);

    /// <summary>The background loop: every <c>Blog__Mail__IntervalSeconds</c> (10; 0 turns it off, as the tests do).</summary>
    public sealed class Worker(MailQueue queue, IConfiguration config, TimeProvider clock, ILogger<Worker> log) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var seconds = config.GetValue("Blog:Mail:IntervalSeconds", 10);
            if (seconds <= 0) return;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await queue.SendPassAsync(clock.GetUtcNow(), stoppingToken); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning(e, "The mail pass failed; it runs again in {Seconds} s.", seconds);
                }
            }
        }
    }
}

/// <summary>
/// The new-post announcement's channel for the Email account (an <see cref="ISocialChannel"/> the announcer drives like
/// the networks): "sending" queues the email for every confirmed subscriber, once. The blog's own queue can always be
/// asked, so a resumed attempt only adds whoever is missing.
/// </summary>
public sealed class EmailChannel(Db db, Guid accountId, TimeProvider clock) : ISocialChannel
{
    public const int MaxText = 5000;

    public string Network => "email";

    public string Id => $"email:{accountId:D}";

    public SocialIdempotency Idempotency { get; } = new(SocialIdempotencyKind.ReRead, null,
        "The blog's own queue: a retry adds nobody twice.");

    public SocialComposition Compose(SocialPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        // The link is the email's button and the tags mean nothing in it: the text is the words alone.
        var text = (post.Text ?? "").Trim();
        var problems = new List<string>();
        if (text.Length == 0) problems.Add("The email is empty.");
        if (text.Length > MaxText) problems.Add($"The email is {text.Length} characters; at most {MaxText}.");
        return new SocialComposition(text, text.Length, MaxText, "characters", problems);
    }

    public SocialAttempt BeginAttempt(SocialPost post) =>
        new(Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)), clock.GetUtcNow());

    public async Task<SocialPostResult> SendAsync(SocialPost post, SocialAttempt attempt, CancellationToken cancellationToken = default)
    {
        var composed = Compose(post);
        if (!composed.Fits) return SocialPostResult.Failed(SocialFailureKind.Rejected, string.Join(" ", composed.Problems));
        if (!post.IdempotencyKey.StartsWith("announcement:", StringComparison.Ordinal)
            || !Guid.TryParse(post.IdempotencyKey["announcement:".Length..], out var announcementId))
            return SocialPostResult.Failed(SocialFailureKind.Rejected, "Not an announcement.");
        var queued = await MailQueue.EnqueuePostAsync(db, announcementId, composed.Text, clock.GetUtcNow(), CancellationToken.None);
        return SocialPostResult.Success($"email:{queued}", null,
            [queued == 0 ? "Nobody has subscribed yet, so no email went out." : $"Queued for {queued} subscriber{(queued == 1 ? "" : "s")}."],
            alreadyPosted: attempt.Resuming);
    }
}

/// <summary>The two emails, in plain words, HTML and text, no tracking and no rewritten links.</summary>
public static class MailTemplates
{
    private static string H(string s) => HtmlEncoder.Default.Encode(s);

    private static string Paragraphs(string text) =>
        string.Concat(text.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => $"<p style=\"margin:0 0 16px\">{H(p.Trim()).Replace("\n", "<br>", StringComparison.Ordinal)}</p>"));

    private static string Shell(string blogTitle, string blogUrl, string inner, string footer) => $"""
        <!doctype html><html><body style="margin:0;padding:24px;background:#f6f5f2;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:#1f1d1a">
        <div style="max-width:560px;margin:0 auto;background:#ffffff;border-radius:8px;padding:28px">
        <p style="margin:0 0 20px;font-size:14px"><a href="{H(blogUrl)}" style="color:#6d675e;text-decoration:none">{H(blogTitle)}</a></p>
        {inner}
        </div>
        <p style="max-width:560px;margin:16px auto 0;font-size:12px;color:#6d675e;line-height:1.5">{footer}</p>
        </body></html>
        """;

    private static string Button(string url, string label) =>
        $"""<p style="margin:24px 0"><a href="{H(url)}" style="display:inline-block;background:#3d5a80;color:#ffffff;text-decoration:none;padding:10px 18px;border-radius:6px">{H(label)}</a></p>""";

    public static Mail Confirm(string from, string fromName, string to, string blogTitle, string blogUrl, string confirmUrl)
    {
        var html = Shell(blogTitle, blogUrl,
            $"<h1 style=\"font-size:20px;margin:0 0 12px\">Confirm your subscription</h1>"
            + $"<p style=\"margin:0 0 16px\">Someone, hopefully you, asked to get new posts from {H(blogTitle)} at this address.</p>"
            + Button(confirmUrl, "Confirm")
            + "<p style=\"margin:0;font-size:14px;color:#6d675e\">The link works for 48 hours. If it wasn't you, ignore this email: nothing is sent until the address is confirmed.</p>",
            $"Sent once, because this address was typed into the form on {H(blogTitle)}.");
        var text = $"Confirm your subscription\n\nSomeone, hopefully you, asked to get new posts from {blogTitle} at this address.\n\nConfirm: {confirmUrl}\n\n"
                   + "The link works for 48 hours. If it wasn't you, ignore this email: nothing is sent until the address is confirmed.\n";
        return new Mail(from, fromName, to, $"Confirm your subscription to {blogTitle}", html, text, new Dictionary<string, string>());
    }

    public static Mail Post(string from, string fromName, string to, string blogTitle, string blogUrl, string title, string postUrl, string body,
        string? byline, string unsubscribeUrl)
    {
        var footer = $"You get this because you subscribed to {H(blogTitle)}. <a href=\"{H(unsubscribeUrl)}\" style=\"color:#6d675e\">Unsubscribe</a>: one click, and your address is erased.";
        var html = Shell(blogTitle, blogUrl,
            $"<h1 style=\"font-size:22px;margin:0 0 8px\"><a href=\"{H(postUrl)}\" style=\"color:#1f1d1a;text-decoration:none\">{H(title)}</a></h1>"
            + (byline is null ? "" : $"<p style=\"margin:0 0 16px;font-size:14px;color:#6d675e\">{H(byline)}</p>")
            + Paragraphs(body)
            + Button(postUrl, "Read it"),
            footer);
        var text = $"{title}\n" + (byline is null ? "" : byline + "\n") + $"\n{body}\n\nRead it: {postUrl}\n\n"
                   + $"You get this because you subscribed to {blogTitle}. Unsubscribe (one click, your address is erased): {unsubscribeUrl}\n";
        return new Mail(from, fromName, to, title, html, text, new Dictionary<string, string>
        {
            // RFC 8058: a mail client's own unsubscribe button POSTs here, and that is enough.
            ["List-Unsubscribe"] = $"<{unsubscribeUrl}>",
            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
        });
    }
}
