using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AgentBlog.Database;
using AgentKit.Channels;
using AgentKit.Fetch;
using AgentKit.Push;

namespace AgentBlog.Notify;

/// <summary>
/// Tells the owner what needs them (docs/specs/notifications.md). The event log is the only source: a cursor walks it
/// and fans each event the owner can be told about out to their channels as deliveries (one per phone for push). A
/// worker sends what is due, judges each message when it sends (a request decided meanwhile is skipped, not sent), and
/// reads the answer with AgentKit.Channels: retried on its schedule, a channel that is gone turned off with the reason
/// and an event, never silently.
/// </summary>
public sealed class Notifier(Db db, ChannelStore channels, IHttpClientFactory http, IConfiguration config, TimeProvider clock, ILogger<Notifier> log)
{
    public const string HttpClientName = "blog-notify";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);
    private const int Batch = 20;

    public sealed record Pass(int Queued, int Sent, int Skipped, int Failed);

    /// <summary>One pass: fan out what is new in the log, then send what is due.</summary>
    public async Task<Pass> RunOnceAsync(CancellationToken ct)
    {
        var queued = await FanOutAsync(ct);
        int sent = 0, skipped = 0, failed = 0;
        foreach (var d in await ClaimAsync(clock.GetUtcNow(), ct))
        {
            switch (await SendAsync(d, ct))
            {
                case "sent": sent++; break;
                case "skipped": skipped++; break;
                default: failed++; break;
            }
        }
        return new Pass(queued, sent, skipped, failed);
    }

    // ------------------------------------------------------------------------------------------------ fan out

    private sealed record LogEvent(long Id, string Kind, string Actor, string? OwnerActor, bool Imported);

    /// <summary>
    /// Reads the log past the cursor (event ids commit in order under the event lock, so none is skipped) and queues a
    /// delivery per event per channel that wants it. The cursor row is locked, so two replicas never queue twice.
    /// </summary>
    public Task<int> FanOutAsync(CancellationToken ct) => db.OperationalAsync(async tx =>
    {
        var from = await tx.One<long>("SELECT last_event_id FROM notify_cursor WHERE name = 'notifier' FOR UPDATE");
        var events = (await tx.Query<LogEvent>("""
            SELECT e.id, e.kind, e.actor,
                   (SELECT 'account:' || m.account_id FROM blog_member m WHERE m.blog_id = e.blog_id AND m.role = 'owner') AS owner_actor,
                   e.imported
            FROM event e WHERE e.id > @from ORDER BY e.id LIMIT 500
            """, new { from })).ToList();
        if (events.Count == 0) return 0;
        // Imported history was news on the instance it came from: the cursor walks past it, nothing is sent.
        var sendable = events.Where(e => !e.Imported && NotifyCatalog.Sendable(e.Kind, e.Actor, e.OwnerActor)).Select(e => e.Id).ToArray();
        var now = clock.GetUtcNow();
        var queued = sendable.Length == 0 ? 0 : await tx.Exec("""
            INSERT INTO delivery (id, blog_id, channel_id, device_id, event_id, next_at, created_at)
            SELECT gen_random_uuid(), c.blog_id, c.id, d.id, e.id, @now, @now
            FROM event e
            JOIN channel c ON c.blog_id = e.blog_id AND c.enabled AND c.removed_at IS NULL AND e.kind = ANY (c.events)
                          AND NOT (e.kind = 'channel_disabled' AND e.target_id = c.id)
                          AND e.at >= c.created_at -- a channel hears what happens after it is made, never older news
            LEFT JOIN push_device d ON c.kind = 'webpush' AND d.channel_id = c.id AND d.gone_at IS NULL
            WHERE e.id = ANY (@sendable) AND (c.kind <> 'webpush' OR d.id IS NOT NULL)
            ON CONFLICT (channel_id, device_id, event_id) WHERE event_id IS NOT NULL DO NOTHING
            """, new { sendable, now });
        await tx.Exec("UPDATE notify_cursor SET last_event_id = @last WHERE name = 'notifier'", new { last = events[^1].Id });
        return queued;
    }, ct);

    // ------------------------------------------------------------------------------------------------- send

    public sealed record Due(Guid Id, Guid BlogId, Guid ChannelId, Guid? DeviceId, long? EventId, int Attempts);

    /// <summary>Takes what is due, leased so another replica (or a pass after a crash) doesn't send it at the same time.</summary>
    private Task<IReadOnlyList<Due>> ClaimAsync(DateTimeOffset now, CancellationToken ct) => db.OperationalAsync(async tx =>
        (IReadOnlyList<Due>)[.. await tx.Query<Due>("""
            UPDATE delivery SET next_at = @lease, attempts = attempts + 1
            WHERE id IN (SELECT id FROM delivery WHERE status IN ('pending', 'failed') AND next_at <= @now
                         ORDER BY next_at LIMIT @Batch FOR UPDATE SKIP LOCKED)
            RETURNING id, blog_id, channel_id, device_id, event_id, attempts
            """, new { now, lease = now + Lease, Batch })], ct);

    /// <summary>A test message to every phone or to one channel, sent at once. Returns how each went.</summary>
    public async Task<IReadOnlyList<DeliveryView>> TestAsync(Guid blogId, Guid channelId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var ids = await db.OperationalAsync(async tx => (await tx.Query<Due>("""
            INSERT INTO delivery (id, blog_id, channel_id, device_id, event_id, next_at, attempts, created_at)
            SELECT gen_random_uuid(), c.blog_id, c.id, d.id, NULL, @lease, 1, @now
            FROM channel c LEFT JOIN push_device d ON c.kind = 'webpush' AND d.channel_id = c.id AND d.gone_at IS NULL
            WHERE c.blog_id = @blogId AND c.id = @channelId AND c.removed_at IS NULL AND (c.kind <> 'webpush' OR d.id IS NOT NULL)
            RETURNING id, blog_id, channel_id, device_id, event_id, attempts
            """, new { blogId, channelId, now, lease = now + Lease })).ToList(), ct);
        foreach (var d in ids) await SendAsync(d, ct, test: true);
        return await RecentAsync(blogId, channelId, ids.Count, ct);
    }

    private sealed record ChannelState(string Kind, string Label, string Detail, bool Enabled, bool Removed, string BlogTitle);

    private async Task<string> SendAsync(Due d, CancellationToken ct, bool test = false)
    {
        var channel = await db.ReadAsync(tx => tx.One<ChannelState>("""
            SELECT c.kind, c.label, c.detail, c.enabled, c.removed_at IS NOT NULL AS removed, b.title AS blog_title
            FROM channel c JOIN blog b ON b.id = c.blog_id WHERE c.id = @ChannelId
            """, d), ct);
        if (channel is null || channel.Removed || (!channel.Enabled && !test))
            return await SkipAsync(d, "The channel is off.", ct);

        ChannelMessage message;
        if (d.EventId is { } eventId)
        {
            var facts = await FactsAsync(eventId, ct);
            if (facts is null) return await SkipAsync(d, "The event is gone.", ct);
            if (facts.Stale is { } stale) return await SkipAsync(d, stale, ct);
            message = Messages.For(facts, channel.Detail, AdminUrl());
        }
        else
        {
            message = Messages.Test(channel.BlogTitle, channel.Label, AdminUrl());
        }

        var secrets = channel.Kind == "webpush" ? new Dictionary<string, string>() : await channels.SecretsAsync(d.BlogId, d.ChannelId, ct);
        if (secrets is null)
            return await RecordAsync(d, channel, new SendOutcome(SendResult.Failed, null, null,
                "Its secret doesn't open (the master key changed?): remove the channel and add it again."), ct);

        HttpRequestMessage request;
        try
        {
            request = channel.Kind switch
            {
                "webpush" => await PushRequestAsync(d, message, ct),
                "discord" => Discord.Request(new Uri(secrets["url"]), message, channel.BlogTitle),
                "slack" => Slack.Request(new Uri(secrets["url"]), message),
                "ntfy" => Ntfy.Request(new Uri(secrets["server"]), secrets["topic"], secrets.GetValueOrDefault("token"), message),
                "webhook" => StandardWebhooks.Request(new Uri(secrets["url"]), secrets["signing_secret"], "dlv_" + d.Id.ToString("N"), clock.GetUtcNow(), message),
                _ => throw new InvalidOperationException($"Unknown channel kind {channel.Kind}."),
            };
        }
        catch (InvalidOperationException e)
        {
            return await RecordAsync(d, channel, new SendOutcome(SendResult.Failed, null, null, e.Message), ct);
        }

        SendOutcome outcome;
        using (request)
        {
            try
            {
                using var client = http.CreateClient(HttpClientName);
                client.Timeout = Timeout;
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                outcome = await SendOutcome.ReadAsync(response, clock.GetUtcNow(), ct);
            }
            catch (Exception e) when (GuardedHttp.Blocked(e) is { } blocked)
            {
                outcome = new SendOutcome(SendResult.Failed, null, null, "Address refused: " + blocked.Message);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                outcome = SendOutcome.NoAnswer(e is TaskCanceledException ? $"No answer within {Timeout.TotalSeconds:0} seconds." : e.Message);
            }
        }
        return await RecordAsync(d, channel, outcome, ct);
    }

    private sealed record DeviceKeys(string Endpoint, byte[] P256dh, byte[] Auth);

    private async Task<HttpRequestMessage> PushRequestAsync(Due d, ChannelMessage m, CancellationToken ct)
    {
        var device = await db.ReadAsync(tx => tx.One<DeviceKeys>(
            "SELECT endpoint, p256dh, auth FROM push_device WHERE id = @DeviceId AND gone_at IS NULL", d), ct)
            ?? throw new InvalidOperationException("The phone is gone.");
        var vapid = await channels.VapidAsync(ct) ?? throw new InvalidOperationException("The push keys don't open (the master key changed?).");
        var payload = new JsonObject { ["title"] = m.Title, ["body"] = m.Body, ["url"] = m.Link?.AbsoluteUri, ["tag"] = m.Type };
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        if (bytes.Length > WebPush.MaxPlaintext)
        {
            payload["title"] = Text.Truncate(m.Title, 200);
            payload["body"] = Text.Truncate(m.Body, 500);
            bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        }
        var endpoint = new Uri(device.Endpoint);
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(WebPush.Encrypt(bytes, device.P256dh, device.Auth)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("Authorization", WebPush.Authorization(endpoint, vapid, clock.GetUtcNow()));
        request.Headers.Add("TTL", "86400");
        request.Headers.Add("Urgency", m.Type == "approval_requested" ? "high" : "normal");
        return request;
    }

    private Task<string> SkipAsync(Due d, string reason, CancellationToken ct) => db.OperationalAsync(async tx =>
    {
        await tx.Exec("UPDATE delivery SET status = 'skipped', error = @reason WHERE id = @Id", new { d.Id, reason });
        return "skipped";
    }, ct);

    /// <summary>
    /// What the answer means. Sent clears the channel's trouble. Retry waits on the schedule (or the destination's
    /// Retry-After), then gives up. Gone: a phone is let go; any other channel is turned off with the reason and a
    /// <c>channel_disabled</c> event, which the owner's other channels then carry.
    /// </summary>
    private async Task<string> RecordAsync(Due d, ChannelState channel, SendOutcome o, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (o.Result == SendResult.Sent)
        {
            await db.OperationalAsync(async tx =>
            {
                await tx.Exec("UPDATE delivery SET status = 'sent', sent_at = @now, http_status = @Status, error = NULL WHERE id = @Id",
                    new { d.Id, o.Status, now });
                return await tx.Exec("UPDATE channel SET failing_since = NULL, last_error = NULL WHERE id = @ChannelId", d);
            }, ct);
            return "sent";
        }

        // A test reports once and stops: retrying it would send the owner a test hours after they stopped looking.
        if (o.Result == SendResult.Retry && d.EventId is not null && Retries.Next(d.Attempts, o.RetryAfter) is { } wait)
        {
            await db.OperationalAsync(async tx =>
            {
                await tx.Exec("UPDATE delivery SET status = 'failed', next_at = @next, http_status = @Status, error = @Error WHERE id = @Id",
                    new { d.Id, o.Status, o.Error, next = now + wait });
                return await Trouble(tx, d.ChannelId, o.Error, now);
            }, ct);
            return "failed";
        }

        var gone = o.Result == SendResult.Gone;
        if (gone && channel.Kind == "webpush" && o.Status is 404 or 410)
        {
            await db.WriteAsync(async tx =>
            {
                await tx.Exec("UPDATE delivery SET status = 'dead', http_status = @Status, error = @Error WHERE id = @Id", new { d.Id, o.Status, o.Error });
                var let = await tx.Exec("""
                    UPDATE push_device SET gone_at = @now, gone_reason = 'The push service says this phone no longer takes notifications.'
                    WHERE id = @DeviceId AND gone_at IS NULL
                    """, new { d.DeviceId, now });
                if (let > 0)
                    await tx.Event(new EventRow(d.BlogId, "push_device", d.DeviceId!.Value, "push_device_gone", Actors.System, new { status = o.Status }), now);
                return 0;
            }, ct);
            return "failed";
        }

        if (gone && channel.Kind != "webpush")
        {
            var reason = $"{Name(channel.Kind)} answered {o.Status}: the webhook was deleted or no longer takes messages. Fix it there, then tap Turn back on (or add it again).";
            await db.WriteAsync(async tx =>
            {
                await tx.Exec("UPDATE delivery SET status = 'dead', http_status = @Status, error = @Error WHERE id = @Id", new { d.Id, o.Status, o.Error });
                var off = await tx.Exec("""
                    UPDATE channel SET enabled = false, disabled_reason = @reason, last_error = @Error, failing_since = coalesce(failing_since, @now)
                    WHERE id = @ChannelId AND enabled
                    """, new { d.ChannelId, reason, o.Error, now });
                if (off > 0)
                {
                    await tx.Exec("""
                        UPDATE delivery SET status = 'skipped', error = 'The channel was turned off.'
                        WHERE channel_id = @ChannelId AND status IN ('pending', 'failed') AND id <> @Id
                        """, d);
                    await tx.Event(new EventRow(d.BlogId, "channel", d.ChannelId, "channel_disabled", Actors.System,
                        new { reason, status = o.Status }), now);
                }
                return 0;
            }, ct);
            log.LogWarning("Notification channel {Channel} turned off: {Reason}", d.ChannelId, reason);
            return "failed";
        }

        // Failed for good, or the retries ran out: dead, and the channel shows why.
        await db.OperationalAsync(async tx =>
        {
            await tx.Exec("UPDATE delivery SET status = 'dead', http_status = @Status, error = @Error WHERE id = @Id", new { d.Id, o.Status, o.Error });
            return await Trouble(tx, d.ChannelId, o.Error, now);
        }, ct);
        return "failed";
    }

    private static Task<int> Trouble(Tx tx, Guid channelId, string? error, DateTimeOffset now) => tx.Exec(
        "UPDATE channel SET failing_since = coalesce(failing_since, @now), last_error = @error WHERE id = @channelId",
        new { channelId, error, now });

    private static string Name(string kind) => kind switch
    {
        "discord" => "Discord",
        "slack" => "Slack",
        "ntfy" => "ntfy",
        "webhook" => "The webhook",
        _ => kind,
    };

    private Task<EventFacts?> FactsAsync(long eventId, CancellationToken ct) => db.ReadAsync(tx => tx.One<EventFacts>("""
        SELECT e.id AS event_id, e.blog_id, e.kind, e.actor, actor_label(e.actor) AS actor_label, e.at, b.title AS blog_title,
               e.target_id, r.title AS post_title, p.id AS post_id, p.status AS post_status,
               coalesce(rq.action, br.action, e.payload->>'action') AS action, coalesce(rq.state, an.state, br.state) AS request_state, e.payload->>'reason' AS reason,
               coalesce(c.level, CASE WHEN e.target_type = 'invitation' THEN e.payload->>'role' END) AS level,
               coalesce(c.label, ch.label, sa.label) AS target_label
        FROM event e
        JOIN blog b ON b.id = e.blog_id
        LEFT JOIN approval_request rq ON e.target_type = 'approval_request' AND rq.id = e.target_id
        LEFT JOIN blog_request br ON e.target_type = 'blog_request' AND br.id = e.target_id
        LEFT JOIN announcement an ON e.target_type = 'announcement' AND an.id = e.target_id
        LEFT JOIN social_account sa ON sa.id = an.account_id
        LEFT JOIN post p ON p.id = CASE WHEN e.target_type = 'post' THEN e.target_id WHEN e.target_type = 'announcement' THEN an.post_id ELSE rq.post_id END
        LEFT JOIN post_revision r ON r.id = p.current_revision_id
        LEFT JOIN connection c ON e.target_type = 'connection' AND c.id = e.target_id
        LEFT JOIN channel ch ON e.target_type = 'channel' AND ch.id = e.target_id
        WHERE e.id = @eventId
        """, new { eventId }), ct);

    private string? AdminUrl() => config["Blog:PublicUrl"] is { Length: > 0 } u ? u.TrimEnd('/') + "/admin/" : null;

    // ------------------------------------------------------------------------------------------------ the log

    public sealed record DeliveryView(Guid Id, Guid ChannelId, Guid? DeviceId, string? Event, string Status, int Attempts,
        int? HttpStatus, string? Error, DateTime CreatedAt, DateTime? SentAt, DateTime NextAt);

    /// <summary>The last deliveries of a channel (or the blog), newest first: what was sent, skipped and why, what failed.</summary>
    public Task<IReadOnlyList<DeliveryView>> RecentAsync(Guid blogId, Guid? channelId, int limit, CancellationToken ct) => db.ReadAsync(async tx =>
        (IReadOnlyList<DeliveryView>)[.. await tx.Query<DeliveryView>("""
            SELECT d.id, d.channel_id, d.device_id, coalesce(e.kind, 'test') AS event, d.status, d.attempts, d.http_status, d.error,
                   d.created_at, d.sent_at, d.next_at
            FROM delivery d LEFT JOIN event e ON e.id = d.event_id
            WHERE d.blog_id = @blogId AND (@channelId::uuid IS NULL OR d.channel_id = @channelId)
            ORDER BY d.created_at DESC, d.id DESC LIMIT @limit
            """, new { blogId, channelId, limit })], ct);

    /// <summary>The background loop: every <c>Blog__Notify__IntervalSeconds</c> (10; 0 turns it off, as the tests do).</summary>
    public sealed class Worker(Notifier notifier, IConfiguration config, TimeProvider clock, ILogger<Worker> log) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var seconds = config.GetValue("Blog:Notify:IntervalSeconds", 10);
            if (seconds <= 0) return;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var pass = await notifier.RunOnceAsync(stoppingToken);
                    if (pass.Sent + pass.Failed > 0)
                        log.LogInformation("Notifications: {Sent} sent, {Failed} failed, {Skipped} skipped", pass.Sent, pass.Failed, pass.Skipped);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A failed pass (a database restart) is retried on the next tick: the cursor and the leases hold.
                    log.LogWarning(e, "The notification pass failed; it runs again in {Seconds} s.", seconds);
                }
            }
        }
    }
}
