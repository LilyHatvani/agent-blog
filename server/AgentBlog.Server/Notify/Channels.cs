using System.Buffers.Text;
using AgentBlog.Database;
using AgentBlog.Posts;
using AgentBlog.Secrets;
using AgentKit.Channels;
using AgentKit.Fetch;
using AgentKit.Push;
using AgentKit.Secrets;

namespace AgentBlog.Notify;

/// <summary>A channel as stored. A class with setters, not a record: Dapper can't pass a <c>text[]</c> to a constructor.</summary>
public sealed class ChannelRow
{
    public Guid Id { get; set; }
    public Guid BlogId { get; set; }
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public string[] Events { get; set; } = [];
    public string Detail { get; set; } = "full";
    public bool Enabled { get; set; }
    public DateTime? FailingSince { get; set; }
    public string? LastError { get; set; }
    public string? DisabledReason { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed record DeviceRow(Guid Id, Guid ChannelId, string Endpoint, byte[] P256dh, byte[] Auth, string Label, DateTime CreatedAt);

/// <summary>What the owner sends to add a channel; which fields matter depends on the kind.</summary>
public sealed record NewChannel(string Kind, string? Label, string[]? Events, string? Detail, string? Url, string? Server, string? Topic, string? Token);

/// <summary>A change to a channel; a field left out is kept.</summary>
public sealed record ChannelChange(string? Label, string[]? Events, string? Detail, bool? Enabled);

/// <summary>
/// The owner's channels (docs/specs/notifications.md): where notices go, which events, how much they say. Secrets (a
/// webhook URL, an ntfy topic and token, a signing secret) are sealed with the blog's master key and never returned,
/// except a webhook's signing secret, once, when it is made. Every change writes an event.
/// </summary>
public sealed class ChannelStore(Db db, Sealer sealer, IConfiguration config, TimeProvider clock)
{
    public static readonly string[] Kinds = ["webpush", "discord", "slack", "ntfy", "webhook"];
    public const int MaxChannels = 20;
    public const int MaxDevices = 20;

    public bool On => sealer.On;

    /// <summary>Why notifications can't be set up here, or null.</summary>
    public string? OffReason => sealer.On ? null : BlogSecrets.OffReason;

    public Task<IReadOnlyList<ChannelRow>> ListAsync(Guid blogId, CancellationToken ct) => db.ReadAsync(async tx =>
        (IReadOnlyList<ChannelRow>)[.. await tx.Query<ChannelRow>("""
            SELECT id, blog_id, kind, label, events, detail, enabled, failing_since, last_error, disabled_reason, created_at
            FROM channel WHERE blog_id = @blogId AND removed_at IS NULL ORDER BY created_at
            """, new { blogId })], ct);

    public Task<ChannelRow?> GetAsync(Guid blogId, Guid channelId, CancellationToken ct) => db.ReadAsync(tx => tx.One<ChannelRow>("""
        SELECT id, blog_id, kind, label, events, detail, enabled, failing_since, last_error, disabled_reason, created_at
        FROM channel WHERE blog_id = @blogId AND id = @channelId AND removed_at IS NULL
        """, new { blogId, channelId }), ct);

    public Task<IReadOnlyList<DeviceRow>> DevicesAsync(Guid blogId, CancellationToken ct) => db.ReadAsync(async tx =>
        (IReadOnlyList<DeviceRow>)[.. await tx.Query<DeviceRow>("""
            SELECT d.id, d.channel_id, d.endpoint, d.p256dh, d.auth, d.label, d.created_at FROM push_device d
            JOIN channel c ON c.id = d.channel_id AND c.removed_at IS NULL
            WHERE d.blog_id = @blogId AND d.gone_at IS NULL ORDER BY d.created_at
            """, new { blogId })], ct);

    /// <summary>The opened secrets of a channel, for sending. Null when one doesn't open (the key is gone, or the row was altered).</summary>
    public async Task<IReadOnlyDictionary<string, string>?> SecretsAsync(Guid blogId, Guid channelId, CancellationToken ct)
    {
        var rows = await db.ReadAsync(tx => tx.Query<SecretRow>("""
            SELECT name, ciphertext, nonce, key_id FROM channel_secret WHERE blog_id = @blogId AND channel_id = @channelId
            """, new { blogId, channelId }), ct);
        var opened = new Dictionary<string, string>();
        foreach (var r in rows)
        {
            if (sealer.OpenText(new SealedValue(r.Ciphertext, r.Nonce, r.KeyId), BlogSecrets.ForChannel(blogId, channelId, r.Name)) is not { } value)
                return null;
            opened[r.Name] = value;
        }
        return opened;
    }

    private sealed record SecretRow(string Name, byte[] Ciphertext, byte[] Nonce, string KeyId);

    /// <summary>A short, safe description of where a channel sends: the host and a few characters, never a usable secret.</summary>
    public static string Hint(string kind, IReadOnlyDictionary<string, string>? secrets)
    {
        if (kind == "webpush") return "this blog's phones";
        if (secrets is null) return "its secret doesn't open: set it up again";
        string Host(string key) => secrets.TryGetValue(key, out var u) && Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Host : "?";
        return kind switch
        {
            "ntfy" => $"{Host("server")} · topic {(secrets.TryGetValue("topic", out var t) ? t[..Math.Min(3, t.Length)] : "")}…"
                      + (secrets.ContainsKey("token") ? " · with a token" : ""),
            _ => $"{Host("url")} · …{(secrets.TryGetValue("url", out var url) ? url[^Math.Min(4, url.Length)..] : "")}",
        };
    }

    /// <summary>Adds a channel. Returns it, and for a webhook its signing secret (shown once), or why not.</summary>
    public async Task<(ChannelRow? Channel, string? SigningSecret, string? Error)> CreateAsync(Actor owner, NewChannel body, CancellationToken ct)
    {
        if (!sealer.On) return (null, null, BlogSecrets.OffReason);
        if (!Kinds.Contains(body.Kind)) return (null, null, $"A channel is one of: {string.Join(", ", Kinds)}.");
        if (body.Kind == "webpush") return (null, null, "Phones are added from the app itself: turn on notifications on the phone.");
        var label = (body.Label ?? "").Trim();
        if (label.Length is 0 or > 100) return (null, null, "A channel needs a name, up to 100 characters.");
        if (Events(body.Events) is var (events, eventsError) && eventsError is not null) return (null, null, eventsError);

        var secrets = new Dictionary<string, string>();
        string? signingSecret = null;
        switch (body.Kind)
        {
            case "discord" or "slack" or "webhook":
                if (!Uri.TryCreate((body.Url ?? "").Trim(), UriKind.Absolute, out var url)) return (null, null, "Paste the full address, starting with https://.");
                var refusal = body.Kind switch
                {
                    "discord" => Discord.WebhookRefusal(url),
                    "slack" => Slack.WebhookRefusal(url),
                    _ => StandardWebhooks.UrlRefusal(url) ?? AddressRefusal(url),
                };
                if (refusal is not null) return (null, null, refusal);
                secrets["url"] = url.AbsoluteUri;
                if (body.Kind == "webhook") secrets["signing_secret"] = signingSecret = StandardWebhooks.NewSecret();
                break;
            case "ntfy":
                var server = Uri.TryCreate((body.Server ?? "").Trim() is { Length: > 0 } s ? s : Ntfy.PublicServer.AbsoluteUri, UriKind.Absolute, out var srv) ? srv : null;
                if (server is null) return (null, null, "An ntfy server is an address such as https://ntfy.sh.");
                if (Ntfy.ServerRefusal(server) is { } badServer) return (null, null, badServer);
                if (AddressRefusal(server) is { } inside) return (null, null, inside);
                if (Ntfy.TopicRefusal((body.Topic ?? "").Trim()) is { } topicError) return (null, null, topicError);
                secrets["server"] = server.AbsoluteUri;
                secrets["topic"] = body.Topic!.Trim();
                if (!string.IsNullOrWhiteSpace(body.Token)) secrets["token"] = body.Token.Trim();
                break;
        }
        if (secrets.Values.Any(v => System.Text.Encoding.UTF8.GetByteCount(v) > BlogSecrets.MaxValueBytes)) return (null, null, "That is too long to keep.");
        var detail = body.Detail ?? (body.Kind == "ntfy" && !secrets.ContainsKey("token") ? "minimal" : "full");
        if (detail is not ("full" or "minimal")) return (null, null, "detail is full or minimal.");

        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        var row = await db.WriteAsync(async tx =>
        {
            if (await tx.One<int>("SELECT count(*) FROM channel WHERE blog_id = @BlogId AND removed_at IS NULL", new { owner.BlogId }) >= MaxChannels)
                return null;
            await tx.Exec("""
                INSERT INTO channel (id, blog_id, kind, label, events, detail, created_by, created_at)
                VALUES (@id, @BlogId, @Kind, @label, @events, @detail, @accountId, @now)
                """, new { id, owner.BlogId, body.Kind, label, events, detail, accountId = owner.AccountId, now });
            foreach (var (name, value) in secrets) await SealAsync(tx, owner.BlogId, id, name, value, now);
            await tx.Event(new EventRow(owner.BlogId, "channel", id, "channel_created", owner.Value,
                new { kind = body.Kind, label, events, detail }), now);
            return await tx.One<ChannelRow>("""
                SELECT id, blog_id, kind, label, events, detail, enabled, failing_since, last_error, disabled_reason, created_at
                FROM channel WHERE id = @id
                """, new { id });
        }, ct);
        return row is null ? (null, null, $"A blog has at most {MaxChannels} channels: remove one first.") : (row, signingSecret, null);
    }

    /// <summary>Changes a channel's name, events, detail or whether it is on. Turning it on again clears why it stopped.</summary>
    public async Task<(ChannelRow? Channel, string? Error)> ChangeAsync(Actor owner, Guid channelId, ChannelChange change, CancellationToken ct)
    {
        var label = change.Label?.Trim();
        if (label is { Length: 0 or > 100 }) return (null, "A channel needs a name, up to 100 characters.");
        string[]? events = null;
        if (change.Events is not null)
        {
            var (e, error) = Events(change.Events);
            if (error is not null) return (null, error);
            events = e;
        }
        if (change.Detail is not (null or "full" or "minimal")) return (null, "detail is full or minimal.");
        var now = clock.GetUtcNow();
        var row = await db.WriteAsync(async tx =>
        {
            var before = await tx.One<ChannelRow>("""
                SELECT id, blog_id, kind, label, events, detail, enabled, failing_since, last_error, disabled_reason, created_at
                FROM channel WHERE blog_id = @BlogId AND id = @channelId AND removed_at IS NULL FOR UPDATE
                """, new { owner.BlogId, channelId });
            if (before is null) return null;
            var changed = new Dictionary<string, object?>();
            if (label is not null && label != before.Label) changed["label"] = label;
            if (events is not null && !events.SequenceEqual(before.Events)) changed["events"] = events;
            if (change.Detail is not null && change.Detail != before.Detail) changed["detail"] = change.Detail;
            if (change.Enabled is { } on && on != before.Enabled) changed["enabled"] = on;
            if (changed.Count == 0) return before;
            await tx.Exec("""
                UPDATE channel SET label = coalesce(@label, label), events = coalesce(@events, events), detail = coalesce(@detail, detail),
                    enabled = coalesce(@enabled, enabled),
                    disabled_reason = CASE WHEN @enabled THEN NULL ELSE disabled_reason END,
                    failing_since = CASE WHEN @enabled THEN NULL ELSE failing_since END,
                    last_error = CASE WHEN @enabled THEN NULL ELSE last_error END
                WHERE id = @channelId
                """, new { label, events, detail = change.Detail, enabled = change.Enabled, channelId });
            await tx.Event(new EventRow(owner.BlogId, "channel", channelId, "channel_changed", owner.Value, changed), now);
            return await tx.One<ChannelRow>("""
                SELECT id, blog_id, kind, label, events, detail, enabled, failing_since, last_error, disabled_reason, created_at
                FROM channel WHERE id = @channelId
                """, new { channelId });
        }, ct);
        return row is null ? (null, "No such channel on this blog.") : (row, null);
    }

    /// <summary>
    /// Removes a channel: it stops at once, its secrets are deleted (what posts somewhere shouldn't outlive the owner's
    /// wish), its phones are let go, and what it sent stays in the log. Pending deliveries are skipped.
    /// </summary>
    public Task<bool> RemoveAsync(Actor owner, Guid channelId, CancellationToken ct) => db.WriteAsync(async tx =>
    {
        var now = clock.GetUtcNow();
        var kind = await tx.One<string>("""
            UPDATE channel SET removed_at = @now, enabled = false WHERE blog_id = @BlogId AND id = @channelId AND removed_at IS NULL RETURNING kind
            """, new { owner.BlogId, channelId, now });
        if (kind is null) return false;
        await tx.Exec("DELETE FROM channel_secret WHERE channel_id = @channelId", new { channelId });
        await tx.Exec("UPDATE push_device SET gone_at = @now, gone_reason = 'The channel was removed.' WHERE channel_id = @channelId AND gone_at IS NULL",
            new { channelId, now });
        await tx.Exec("""
            UPDATE delivery SET status = 'skipped', error = 'The channel was removed.' WHERE channel_id = @channelId AND status IN ('pending', 'failed')
            """, new { channelId });
        await tx.Event(new EventRow(owner.BlogId, "channel", channelId, "channel_removed", owner.Value, new { kind }), now);
        return true;
    }, ct);

    /// <summary>
    /// A phone turns push on (the browser's <c>PushSubscription.toJSON()</c>). The blog's push channel is made on the
    /// first phone, with the default events. The same endpoint again replaces its keys.
    /// </summary>
    public async Task<(DeviceRow? Device, string? Error)> AddDeviceAsync(Actor owner, string? endpoint, string? p256dh, string? auth, string? label, CancellationToken ct)
    {
        if (!sealer.On) return (null, BlogSecrets.OffReason);
        if (!Uri.TryCreate(endpoint ?? "", UriKind.Absolute, out var uri) || endpoint!.Length > 2000)
            return (null, "endpoint must be the push service's https URL.");
        if (PublicAddress.Refusal(uri, httpsOnly: true) is { } why) return (null, "endpoint: " + why);
        byte[] key, secret;
        try
        {
            key = Base64Url.DecodeFromChars(p256dh ?? "");
            secret = Base64Url.DecodeFromChars(auth ?? "");
        }
        catch (FormatException)
        {
            return (null, "keys.p256dh and keys.auth are base64url.");
        }
        if (WebPush.SubscriptionRefusal(key, secret) is { } bad) return (null, bad);
        label = (label ?? "").Trim();
        if (label.Length > 100) return (null, "A phone's name is up to 100 characters.");
        await VapidAsync(ct); // made before the first phone subscribes with it

        var now = clock.GetUtcNow();
        var device = await db.WriteAsync(async tx =>
        {
            // One push channel per blog; the row lock on the blog makes two first phones at once agree on it.
            await tx.Exec("SELECT 1 FROM blog WHERE id = @BlogId FOR UPDATE", new { owner.BlogId });
            var channelId = await tx.One<Guid?>("SELECT id FROM channel WHERE blog_id = @BlogId AND kind = 'webpush' AND removed_at IS NULL", new { owner.BlogId });
            if (channelId is null)
            {
                channelId = Guid.CreateVersion7();
                await tx.Exec("""
                    INSERT INTO channel (id, blog_id, kind, label, events, detail, created_by, created_at)
                    VALUES (@channelId, @BlogId, 'webpush', 'Phones', @events, 'full', @accountId, @now)
                    """, new { channelId, owner.BlogId, events = NotifyCatalog.Defaults, accountId = owner.AccountId, now });
                await tx.Event(new EventRow(owner.BlogId, "channel", channelId.Value, "channel_created", owner.Value,
                    new { kind = "webpush", label = "Phones", events = NotifyCatalog.Defaults, detail = "full" }), now);
            }
            var existing = await tx.One<Guid?>("SELECT id FROM push_device WHERE channel_id = @channelId AND endpoint = @endpoint AND gone_at IS NULL",
                new { channelId, endpoint });
            if (existing is { } same)
            {
                await tx.Exec("UPDATE push_device SET p256dh = @key, auth = @secret, label = @label WHERE id = @same", new { key, secret, label, same });
                return await DeviceAsync(tx, same);
            }
            if (await tx.One<int>("SELECT count(*) FROM push_device WHERE channel_id = @channelId AND gone_at IS NULL", new { channelId }) >= MaxDevices)
                return null;
            var id = Guid.CreateVersion7();
            await tx.Exec("""
                INSERT INTO push_device (id, blog_id, channel_id, endpoint, p256dh, auth, label, created_at)
                VALUES (@id, @BlogId, @channelId, @endpoint, @key, @secret, @label, @now)
                """, new { id, owner.BlogId, channelId, endpoint, key, secret, label, now });
            await tx.Event(new EventRow(owner.BlogId, "push_device", id, "push_device_added", owner.Value, new { label }), now);
            return await DeviceAsync(tx, id);
        }, ct);
        return device is null ? (null, $"At most {MaxDevices} phones get notifications: remove one first.") : (device, null);
    }

    private static Task<DeviceRow?> DeviceAsync(Tx tx, Guid id) => tx.One<DeviceRow>(
        "SELECT id, channel_id, endpoint, p256dh, auth, label, created_at FROM push_device WHERE id = @id", new { id });

    /// <summary>A phone stops getting notifications (the owner removes it, or the phone turns push off).</summary>
    public Task<bool> RemoveDeviceAsync(Actor owner, Guid? deviceId, string? endpoint, CancellationToken ct) => db.WriteAsync(async tx =>
    {
        var now = clock.GetUtcNow();
        var id = await tx.One<Guid?>("""
            UPDATE push_device SET gone_at = @now, gone_reason = 'Turned off by the owner.'
            WHERE blog_id = @BlogId AND gone_at IS NULL AND (id = @deviceId OR endpoint = @endpoint) RETURNING id
            """, new { owner.BlogId, deviceId, endpoint, now });
        if (id is null) return false;
        await tx.Exec("""
            UPDATE delivery SET status = 'skipped', error = 'The phone was removed.' WHERE device_id = @id AND status IN ('pending', 'failed')
            """, new { id });
        await tx.Event(new EventRow(owner.BlogId, "push_device", id.Value, "push_device_removed", owner.Value, null), now);
        return true;
    }, ct);

    /// <summary>
    /// The instance's VAPID pair, made and sealed on first use. Its subject is <c>Blog__Notify__Subject</c>, else the
    /// blog's public address. Changing the pair would cut off every phone, so it is made once and kept.
    /// </summary>
    public async Task<VapidKeys?> VapidAsync(CancellationToken ct)
    {
        if (!sealer.On) return null;
        var subject = Subject();
        if (await ReadVapidAsync(subject, ct) is { } existing) return existing;
        var fresh = VapidKeys.Generate(subject);
        var sealedKey = sealer.Seal(fresh.PrivateKeyText, BlogSecrets.ForInstance("vapid"));
        var stored = await db.ReadAsync(tx => tx.One<VapidRow>("SELECT public, ciphertext, nonce, key_id FROM instance_secret WHERE name = 'vapid'"), ct);
        var args = new { pub = fresh.PublicKeyText, sealedKey.Ciphertext, sealedKey.Nonce, sealedKey.KeyId, now = clock.GetUtcNow(), old = stored?.Ciphertext };
        if (stored is null)
            await db.OperationalAsync(tx => tx.Exec("""
                INSERT INTO instance_secret (name, public, ciphertext, nonce, key_id, created_at)
                VALUES ('vapid', @pub, @Ciphertext, @Nonce, @KeyId, @now) ON CONFLICT (name) DO NOTHING
                """, args), ct);
        else
            // The stored pair doesn't open any more (the master key that sealed it is gone): without a replacement push
            // would stay off for good. Every phone subscribed under the old public key can't be reached with the new
            // one, so each is marked gone, with the reason, and turns notifications on again. Only the row this
            // replica read is replaced: a replica that replaced it first wins.
            await db.OperationalAsync(async tx =>
            {
                if (await tx.Exec("""
                    UPDATE instance_secret SET public = @pub, ciphertext = @Ciphertext, nonce = @Nonce, key_id = @KeyId, created_at = @now
                    WHERE name = 'vapid' AND ciphertext = @old
                    """, args) == 1)
                    await tx.Exec("""
                        UPDATE push_device SET gone_at = @now,
                            gone_reason = 'The server''s notification key was replaced: turn notifications on again on this phone.'
                        WHERE gone_at IS NULL
                        """, args);
                return 0;
            }, ct);
        return await ReadVapidAsync(subject, ct); // another replica may have won: use what is stored
    }

    private async Task<VapidKeys?> ReadVapidAsync(string subject, CancellationToken ct)
    {
        var row = await db.ReadAsync(tx => tx.One<VapidRow>(
            "SELECT public, ciphertext, nonce, key_id FROM instance_secret WHERE name = 'vapid'"), ct);
        if (row is null) return null;
        var priv = sealer.OpenText(new SealedValue(row.Ciphertext, row.Nonce, row.KeyId), BlogSecrets.ForInstance("vapid"));
        return priv is null ? null : VapidKeys.TryParse(row.Public, priv, subject, out _);
    }

    private sealed record VapidRow(string Public, byte[] Ciphertext, byte[] Nonce, string KeyId);

    private string Subject() =>
        config["Blog:Notify:Subject"] is { Length: > 0 } s ? s
        : config["Blog:PublicUrl"] is { Length: > 0 } u && u.StartsWith("https://", StringComparison.Ordinal) ? u.TrimEnd('/')
        : "https://agent-blog.invalid";

    private async Task SealAsync(Tx tx, Guid blogId, Guid channelId, string name, string value, DateTimeOffset now)
    {
        var v = sealer.Seal(value, BlogSecrets.ForChannel(blogId, channelId, name));
        await tx.Exec("""
            INSERT INTO channel_secret (blog_id, channel_id, name, ciphertext, nonce, key_id, created_at)
            VALUES (@blogId, @channelId, @name, @Ciphertext, @Nonce, @KeyId, @now)
            """, new { blogId, channelId, name, v.Ciphertext, v.Nonce, v.KeyId, now });
    }

    private static (string[] Events, string? Error) Events(string[]? asked)
    {
        if (asked is null) return (NotifyCatalog.Defaults, null);
        var events = asked.Distinct().ToArray();
        if (events.Length == 0) return ([], "Choose at least one event; to stop a channel, turn it off.");
        if (events.FirstOrDefault(e => !NotifyCatalog.Kinds.Contains(e)) is { } unknown)
            return ([], $"'{unknown}' isn't an event a channel can send: {string.Join(", ", NotifyCatalog.Kinds)}.");
        return (events, null);
    }

    /// <summary>A literal private address is refused when the channel is made, unless this server allows its own network.</summary>
    private string? AddressRefusal(Uri url) =>
        config.GetValue("Blog:Notify:AllowPrivate", false) ? null : PublicAddress.Refusal(url);
}
