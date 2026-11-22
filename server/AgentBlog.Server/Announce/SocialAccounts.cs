using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentBlog.Database;
using AgentBlog.Posts;
using AgentBlog.Secrets;
using AgentKit.Fetch;
using AgentKit.Secrets;
using AgentKit.Social;

namespace AgentBlog.Announce;

/// <summary>An account as stored: never its credentials. A class with setters, as Dapper wants.</summary>
public sealed class SocialAccountRow
{
    public Guid Id { get; set; }
    public Guid BlogId { get; set; }
    public string Network { get; set; } = "";
    public string Label { get; set; } = "";
    public string Settings { get; set; } = "{}";
    public bool Auto { get; set; }
    public bool Enabled { get; set; }
    public string? LastError { get; set; }
    public DateTime? FailingSince { get; set; }
    public DateTime CreatedAt { get; set; }

    public JsonObject SettingsJson => JsonNode.Parse(Settings) as JsonObject ?? [];

    public string? Setting(string name) => SettingsJson[name]?.GetValue<string>();
}

/// <summary>What the owner pastes to add an account; which fields matter depends on the network (docs/specs/announce.md § Accounts).</summary>
public sealed record NewSocialAccount(string Network, string? Label, string? Handle, string? Password, string? Instance, string? Token,
    string? Url, string? Chat, string? ApiKey, string? ApiSecret, string? AccessToken, string? AccessSecret);

/// <summary>
/// The owner's social accounts (docs/specs/announce.md): where announcements go. Credentials are sealed with the blog's
/// master key, one row per value with associated data naming the account, and never returned. Only the owner adds,
/// changes or removes one; every change writes an event.
/// </summary>
public sealed partial class SocialAccounts(Db db, Sealer sealer, SocialChannels channels, IHttpClientFactory http, TimeProvider clock)
{
    public const string ProbeHttpClientName = "blog-social-probe";

    public static readonly string[] Networks = ["bluesky", "mastodon", "discord", "slack", "telegram", "x"];
    public const int MaxAccounts = 20;

    public bool On => sealer.On;

    public string? OffReason => sealer.On ? null : BlogSecrets.OffReason;

    private const string Select = """
        SELECT id, blog_id, network, label, settings::text AS settings, auto, enabled, last_error, failing_since, created_at
        FROM social_account
        """;

    public Task<IReadOnlyList<SocialAccountRow>> ListAsync(Guid blogId, CancellationToken ct) => db.ReadAsync(async tx =>
        (IReadOnlyList<SocialAccountRow>)[.. await tx.Query<SocialAccountRow>(
            $"{Select} WHERE blog_id = @blogId AND removed_at IS NULL ORDER BY created_at", new { blogId })], ct);

    public Task<SocialAccountRow?> GetAsync(Guid blogId, Guid accountId, CancellationToken ct) => db.ReadAsync(tx => tx.One<SocialAccountRow>(
        $"{Select} WHERE blog_id = @blogId AND id = @accountId AND removed_at IS NULL", new { blogId, accountId }), ct);

    /// <summary>The account's channel, made from its opened credentials; null when they don't open (the key is gone, or the row was altered).</summary>
    public async Task<ISocialChannel?> ChannelAsync(SocialAccountRow account, CancellationToken ct)
    {
        var rows = await db.ReadAsync(tx => tx.Query<SecretRow>(
            "SELECT name, ciphertext, nonce, key_id FROM social_secret WHERE blog_id = @BlogId AND account_id = @Id", account), ct);
        var s = new Dictionary<string, string>();
        foreach (var r in rows)
        {
            if (sealer.OpenText(new SealedValue(r.Ciphertext, r.Nonce, r.KeyId), ForAccount(account.BlogId, account.Id, r.Name)) is not { } value)
                return null;
            s[r.Name] = value;
        }
        try
        {
            return Make(account.Network, account.SettingsJson, s);
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException)
        {
            return null;
        }
    }

    private ISocialChannel Make(string network, JsonObject settings, IReadOnlyDictionary<string, string> s)
    {
        int Int(string name, int fallback) => settings[name] is JsonValue v && v.TryGetValue<int>(out var i) && i > 0 ? i : fallback;
        return network switch
        {
            "bluesky" => channels.Bluesky(new BlueskyAccount(s["handle"], s["app_password"])),
            "mastodon" => channels.Mastodon(new MastodonAccount(new Uri(s["instance"]), s["token"])
            {
                MaxCharacters = Int("max_characters", 500),
                CharactersReservedPerUrl = Int("characters_reserved_per_url", 23),
                MaxMediaAttachments = Int("max_media_attachments", 4),
            }),
            "discord" => channels.Discord(new DiscordWebhook(new Uri(s["url"]))),
            "slack" => channels.Slack(new SlackWebhook(new Uri(s["url"]))),
            "telegram" => channels.Telegram(new TelegramChat(s["token"], s["chat"])),
            "x" => channels.X(new XAccount(s["api_key"], s["api_secret"], s["access_token"], s["access_secret"])),
            _ => throw new ArgumentException($"No network {network}."),
        };
    }

    private sealed record SecretRow(string Name, byte[] Ciphertext, byte[] Nonce, string KeyId);

    public static byte[] ForAccount(Guid blogId, Guid accountId, string name) =>
        Sealer.AssociatedData("agentblog-social-secret:v1", blogId.ToString(), accountId.ToString(), name);

    [GeneratedRegex("^[a-z0-9]{4}(-[a-z0-9]{4}){3}$")]
    private static partial Regex AppPassword();

    /// <summary>Adds an account. Returns it, or why not, in words for the owner.</summary>
    public async Task<(SocialAccountRow? Account, string? Error)> CreateAsync(Actor owner, NewSocialAccount body, CancellationToken ct)
    {
        if (!sealer.On) return (null, BlogSecrets.OffReason);
        if (!Networks.Contains(body.Network)) return (null, $"An account is on one of: {string.Join(", ", Networks)}.");
        static string T(string? v) => (v ?? "").Trim();
        var secrets = new Dictionary<string, string>();
        var settings = new JsonObject();
        string label;
        switch (body.Network)
        {
            case "bluesky":
                var handle = T(body.Handle).TrimStart('@');
                if (handle.Length is < 3 or > 253) return (null, "Your Bluesky handle, such as name.bsky.social.");
                var password = T(body.Password);
                // An app password is four groups of four; anything else is most likely the account's own password, which
                // must never be kept here.
                if (!AppPassword().IsMatch(password))
                    return (null, "Use an app password (Settings, Privacy and security, App passwords: xxxx-xxxx-xxxx-xxxx), never your account password.");
                secrets["handle"] = handle;
                secrets["app_password"] = password;
                settings["handle"] = handle;
                label = "@" + handle;
                break;
            case "mastodon":
                if (!Uri.TryCreate(T(body.Instance) is { Length: > 0 } i && !i.Contains("://", StringComparison.Ordinal) ? "https://" + i : T(body.Instance),
                        UriKind.Absolute, out var instance) || instance.Scheme != Uri.UriSchemeHttps || PublicAddress.Refusal(instance) is not null)
                    return (null, "The instance's address, such as mastodon.social.");
                if (T(body.Token).Length is 0 or > 500) return (null, "An access token from Preferences, Development, with write:statuses and write:media.");
                var root = new Uri(instance.GetLeftPart(UriPartial.Authority));
                secrets["instance"] = root.AbsoluteUri;
                secrets["token"] = T(body.Token);
                settings["instance"] = root.Host;
                foreach (var (k, v) in await InstanceLimitsAsync(root, ct)) settings[k] = v;
                label = root.Host;
                break;
            case "discord" or "slack":
                if (!Uri.TryCreate(T(body.Url), UriKind.Absolute, out var url)) return (null, "Paste the full webhook address, starting with https://.");
                try
                {
                    if (body.Network == "discord") settings["webhook"] = new DiscordWebhook(url).WebhookId;
                    else _ = new SlackWebhook(url);
                }
                catch (ArgumentException e)
                {
                    return (null, e.Message.Split(" (Parameter", 2)[0]);
                }
                secrets["url"] = url.AbsoluteUri;
                label = body.Network == "discord" ? "Discord channel" : "Slack channel";
                break;
            case "telegram":
                try
                {
                    var chat = new TelegramChat(T(body.Token), T(body.Chat));
                    secrets["token"] = T(body.Token);
                    secrets["chat"] = chat.Chat;
                    settings["chat"] = chat.Chat;
                    label = chat.Chat;
                }
                catch (ArgumentException e)
                {
                    return (null, e.Message.Split(" (Parameter", 2)[0]);
                }
                break;
            default: // x
                if (new[] { body.ApiKey, body.ApiSecret, body.AccessToken, body.AccessSecret }.Any(v => T(v).Length is 0 or > 200))
                    return (null, "X needs the app's API key and secret and its access token and secret, generated with Read and write.");
                secrets["api_key"] = T(body.ApiKey);
                secrets["api_secret"] = T(body.ApiSecret);
                secrets["access_token"] = T(body.AccessToken);
                secrets["access_secret"] = T(body.AccessSecret);
                label = "X";
                break;
        }
        if (T(body.Label) is { Length: > 0 } given) label = given;
        if (label.Length > 100) return (null, "A name is up to 100 characters.");
        if (secrets.Values.Any(v => System.Text.Encoding.UTF8.GetByteCount(v) > BlogSecrets.MaxValueBytes)) return (null, "That is too long to keep.");
        try
        {
            _ = Make(body.Network, settings, secrets);
        }
        catch (ArgumentException e)
        {
            return (null, e.Message.Split(" (Parameter", 2)[0]);
        }

        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        var row = await db.WriteAsync(async tx =>
        {
            if (await tx.One<int>("SELECT count(*) FROM social_account WHERE blog_id = @BlogId AND removed_at IS NULL", new { owner.BlogId }) >= MaxAccounts)
                return null;
            await tx.Exec("""
                INSERT INTO social_account (id, blog_id, network, label, settings, created_by, created_at)
                VALUES (@id, @BlogId, @Network, @label, @settings::jsonb, @accountId, @now)
                """, new { id, owner.BlogId, body.Network, label, settings = settings.ToJsonString(), accountId = owner.AccountId, now });
            foreach (var (name, value) in secrets)
            {
                var sealedValue = sealer.Seal(value, ForAccount(owner.BlogId, id, name));
                await tx.Exec("""
                    INSERT INTO social_secret (blog_id, account_id, name, ciphertext, nonce, key_id, created_at)
                    VALUES (@BlogId, @id, @name, @Ciphertext, @Nonce, @KeyId, @now)
                    """, new { owner.BlogId, id, name, sealedValue.Ciphertext, sealedValue.Nonce, sealedValue.KeyId, now });
            }
            await tx.Event(new EventRow(owner.BlogId, "social_account", id, "social_account_created", owner.Value,
                new { network = body.Network, label }), now);
            return await tx.One<SocialAccountRow>($"{Select} WHERE id = @id", new { id });
        }, ct);
        return row is null ? (null, $"A blog has at most {MaxAccounts} accounts: remove one first.") : (row, null);
    }

    /// <summary>
    /// The instance's own limits (<c>GET /api/v2/instance</c>, <c>configuration.statuses</c> and
    /// <c>configuration.media_attachments</c>), read once when the account is added. Mastodon's defaults when it doesn't say.
    /// </summary>
    private async Task<Dictionary<string, int>> InstanceLimitsAsync(Uri root, CancellationToken ct)
    {
        var limits = new Dictionary<string, int>();
        try
        {
            using var client = http.CreateClient(ProbeHttpClientName);
            client.Timeout = TimeSpan.FromSeconds(10);
            using var doc = JsonDocument.Parse(await client.GetStringAsync(new Uri(root, "/api/v2/instance"), ct));
            if (doc.RootElement.TryGetProperty("configuration", out var c))
            {
                if (c.TryGetProperty("statuses", out var st))
                {
                    if (st.TryGetProperty("max_characters", out var m) && m.TryGetInt32(out var mi) && mi is > 0 and < 100_000) limits["max_characters"] = mi;
                    if (st.TryGetProperty("characters_reserved_per_url", out var u) && u.TryGetInt32(out var ui) && ui is > 0 and < 1000) limits["characters_reserved_per_url"] = ui;
                    if (st.TryGetProperty("max_media_attachments", out var a) && a.TryGetInt32(out var ai) && ai is > 0 and < 100) limits["max_media_attachments"] = ai;
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            // Mastodon's own defaults stand; the owner's first post shows if the instance takes less.
        }
        return limits;
    }

    /// <summary>Changes an account's name, whether it announces new posts, or whether it is on. Turning it on clears its error.</summary>
    public async Task<SocialAccountRow?> ChangeAsync(Actor owner, Guid accountId, string? label, bool? auto, bool? enabled, CancellationToken ct)
    {
        label = label?.Trim();
        if (label is { Length: 0 or > 100 }) return null;
        var now = clock.GetUtcNow();
        return await db.WriteAsync(async tx =>
        {
            var before = await tx.One<SocialAccountRow>($"{Select} WHERE blog_id = @BlogId AND id = @accountId AND removed_at IS NULL FOR UPDATE",
                new { owner.BlogId, accountId });
            if (before is null) return null;
            var changed = new Dictionary<string, object?>();
            if (label is not null && label != before.Label) changed["label"] = label;
            if (auto is { } a && a != before.Auto) changed["auto"] = a;
            if (enabled is { } e && e != before.Enabled) changed["enabled"] = e;
            if (changed.Count == 0) return before;
            await tx.Exec("""
                UPDATE social_account SET label = coalesce(@label, label), auto = coalesce(@auto, auto), enabled = coalesce(@enabled, enabled),
                    last_error = CASE WHEN @enabled THEN NULL ELSE last_error END,
                    failing_since = CASE WHEN @enabled THEN NULL ELSE failing_since END
                WHERE id = @accountId
                """, new { label, auto, enabled, accountId });
            await tx.Event(new EventRow(owner.BlogId, "social_account", accountId, "social_account_changed", owner.Value, changed), now);
            return await tx.One<SocialAccountRow>($"{Select} WHERE id = @accountId", new { accountId });
        }, ct);
    }

    /// <summary>Removes an account: its credentials are deleted and what was waiting to go to it is cancelled.</summary>
    public async Task<bool> RemoveAsync(Actor owner, Guid accountId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.WriteAsync(async tx =>
        {
            var removed = await tx.Exec("""
                UPDATE social_account SET removed_at = @now, enabled = false WHERE blog_id = @BlogId AND id = @accountId AND removed_at IS NULL
                """, new { owner.BlogId, accountId, now });
            if (removed == 0) return false;
            await tx.Exec("DELETE FROM social_secret WHERE account_id = @accountId", new { accountId });
            var cancelled = await tx.Query<Guid>("""
                UPDATE announcement SET state = 'cancelled', error = 'The account was removed.', updated_at = @now, lease_until = NULL
                WHERE account_id = @accountId AND state IN ('draft', 'waiting', 'scheduled', 'failed', 'unconfirmed')
                RETURNING id
                """, new { accountId, now });
            foreach (var id in cancelled.ToList())
                await tx.Event(new EventRow(owner.BlogId, "announcement", id, "announcement_cancelled", owner.Value, new { reason = "account_removed" }), now);
            await tx.Event(new EventRow(owner.BlogId, "social_account", accountId, "social_account_removed", owner.Value, new { cancelled = cancelled.Count() }), now);
            return true;
        }, ct);
    }

    /// <summary>Where an account posts, in words that hold no secret.</summary>
    public static string Hint(SocialAccountRow a) => a.Network switch
    {
        "bluesky" => $"@{a.Setting("handle")}",
        "mastodon" => a.Setting("instance") ?? "",
        "discord" => $"webhook {a.Setting("webhook")}",
        "slack" => "webhook",
        "telegram" => a.Setting("chat") ?? "",
        "x" => "the app's keys",
        _ => a.Network,
    };
}
