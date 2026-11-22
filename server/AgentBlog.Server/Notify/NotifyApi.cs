using System.Text.Json.Serialization;
using AgentBlog.Auth;
using AgentBlog.Database;
using AgentBlog.Posts;

namespace AgentBlog.Notify;

/// <summary>The owner's side of notifications (docs/specs/notifications.md): channels, phones, a test, the log. Session only.</summary>
public static class NotifyApi
{
    public sealed record ChannelBody(
        [property: JsonPropertyName("kind")] string? Kind,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("events")] string[]? Events,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("server")] string? Server,
        [property: JsonPropertyName("topic")] string? Topic,
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("enabled")] bool? Enabled);

    public sealed record Keys([property: JsonPropertyName("p256dh")] string? P256dh, [property: JsonPropertyName("auth")] string? Auth);

    public sealed record DeviceBody(
        [property: JsonPropertyName("endpoint")] string? Endpoint,
        [property: JsonPropertyName("keys")] Keys? Keys,
        [property: JsonPropertyName("label")] string? Label);

    public static void MapNotifyApi(this WebApplication app)
    {
        var api = app.MapOwnerApi();

        api.MapGet("/blogs/{blogId:guid}/notifications", async (HttpContext ctx, Guid blogId, ChannelStore store, Notifier notifier, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var ct = ctx.RequestAborted;
            var list = await store.ListAsync(blogId, ct);
            var devices = await store.DevicesAsync(blogId, ct);
            var recent = await notifier.RecentAsync(blogId, null, 60, ct);
            var vapid = store.On ? await store.VapidAsync(ct) : null;
            var hints = new Dictionary<Guid, string>();
            foreach (var c in list) hints[c.Id] = ChannelStore.Hint(c.Kind, c.Kind == "webpush" ? null : await store.SecretsAsync(blogId, c.Id, ct));
            return Results.Json(new
            {
                available = store.On,
                reason = store.OffReason,
                vapid_public_key = vapid?.PublicKeyText,
                events = NotifyCatalog.Events.Select(e => new { kind = e.Kind, label = e.Label, @default = e.Default }),
                kinds = ChannelStore.Kinds,
                channels = list.Select(c => ChannelJson(c, hints[c.Id],
                    devices.Where(d => d.ChannelId == c.Id).Select(DeviceJson),
                    recent.Where(r => r.ChannelId == c.Id).Take(20).Select(DeliveryJson))),
            });
        });

        api.MapPost("/blogs/{blogId:guid}/channels", async (HttpContext ctx, Guid blogId, ChannelBody body, ChannelStore store, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var (channel, signingSecret, error) = await store.CreateAsync(Owner(ctx, blogId),
                new NewChannel(body.Kind ?? "", body.Label, body.Events, body.Detail, body.Url, body.Server, body.Topic, body.Token), ctx.RequestAborted);
            if (error is not null) return SignIn.Problem(store.On ? 400 : 409, store.On ? "invalid_argument" : "secrets_off", error);
            var secrets = await store.SecretsAsync(blogId, channel!.Id, ctx.RequestAborted);
            return Results.Json(new
            {
                channel = ChannelJson(channel, ChannelStore.Hint(channel.Kind, secrets), [], []),
                // Shown once: whatever receives the webhook verifies with it. It can't be read back.
                signing_secret = signingSecret,
            });
        });

        api.MapPatch("/blogs/{blogId:guid}/channels/{channelId:guid}", async (HttpContext ctx, Guid blogId, Guid channelId, ChannelBody body, ChannelStore store, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            if (body.Url is not null || body.Server is not null || body.Topic is not null || body.Token is not null || body.Kind is not null)
                return SignIn.Problem(400, "invalid_argument", "Where a channel sends can't be changed: remove it and add a new one.");
            var (channel, error) = await store.ChangeAsync(Owner(ctx, blogId), channelId, new ChannelChange(body.Label, body.Events, body.Detail, body.Enabled), ctx.RequestAborted);
            if (channel is null) return SignIn.Problem(error == "No such channel on this blog." ? 404 : 400, error == "No such channel on this blog." ? "not_found" : "invalid_argument", error!);
            var secrets = channel.Kind == "webpush" ? null : await store.SecretsAsync(blogId, channel.Id, ctx.RequestAborted);
            return Results.Json(new { channel = ChannelJson(channel, ChannelStore.Hint(channel.Kind, secrets), [], []) });
        });

        api.MapDelete("/blogs/{blogId:guid}/channels/{channelId:guid}", async (HttpContext ctx, Guid blogId, Guid channelId, ChannelStore store, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            return await store.RemoveAsync(Owner(ctx, blogId), channelId, ctx.RequestAborted)
                ? Results.NoContent() : SignIn.Problem(404, "not_found", "No such channel on this blog.");
        });

        api.MapPost("/blogs/{blogId:guid}/channels/{channelId:guid}/test", async (HttpContext ctx, Guid blogId, Guid channelId, ChannelStore store, Notifier notifier, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var channel = await store.GetAsync(blogId, channelId, ctx.RequestAborted);
            if (channel is null) return SignIn.Problem(404, "not_found", "No such channel on this blog.");
            var results = await notifier.TestAsync(blogId, channelId, ctx.RequestAborted);
            if (results.Count == 0) return SignIn.Problem(409, "no_devices", "No phone gets notifications yet: turn them on on the phone first.");
            return Results.Json(new { deliveries = results.Select(DeliveryJson) });
        });

        api.MapPost("/blogs/{blogId:guid}/push/devices", async (HttpContext ctx, Guid blogId, DeviceBody body, ChannelStore store, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var (device, error) = await store.AddDeviceAsync(Owner(ctx, blogId), body.Endpoint, body.Keys?.P256dh, body.Keys?.Auth,
                body.Label ?? DeviceName(ctx.Request.Headers.UserAgent.ToString()), ctx.RequestAborted);
            return device is null
                ? SignIn.Problem(store.On ? 400 : 409, store.On ? "invalid_argument" : "secrets_off", error!)
                : Results.Json(new { device = DeviceJson(device) });
        });

        api.MapPost("/blogs/{blogId:guid}/push/devices/remove", async (HttpContext ctx, Guid blogId, DeviceBody body, ChannelStore store, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            return await store.RemoveDeviceAsync(Owner(ctx, blogId), null, body.Endpoint, ctx.RequestAborted)
                ? Results.NoContent() : SignIn.Problem(404, "not_found", "This phone doesn't get notifications.");
        });

        api.MapDelete("/blogs/{blogId:guid}/push/devices/{deviceId:guid}", async (HttpContext ctx, Guid blogId, Guid deviceId, ChannelStore store, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            return await store.RemoveDeviceAsync(Owner(ctx, blogId), deviceId, null, ctx.RequestAborted)
                ? Results.NoContent() : SignIn.Problem(404, "not_found", "No such phone.");
        });
    }

    private static object ChannelJson(ChannelRow c, string hint, IEnumerable<object> devices, IEnumerable<object> recent) => new
    {
        id = c.Id, kind = c.Kind, label = c.Label, events = c.Events, detail = c.Detail, enabled = c.Enabled, hint,
        failing_since = c.FailingSince, last_error = c.LastError, disabled_reason = c.DisabledReason, created_at = c.CreatedAt,
        devices, recent,
    };

    private static object DeviceJson(DeviceRow d) => new
    {
        id = d.Id, label = d.Label, created_at = d.CreatedAt,
        // The browser compares this with its own subscription to know whether this phone is the one listed.
        endpoint_tail = d.Endpoint.Length > 12 ? d.Endpoint[^12..] : d.Endpoint,
    };

    private static object DeliveryJson(Notifier.DeliveryView d) => new
    {
        id = d.Id, channel = d.ChannelId, device = d.DeviceId, @event = d.Event, status = d.Status, attempts = d.Attempts,
        http_status = d.HttpStatus, error = d.Error, created_at = d.CreatedAt, sent_at = d.SentAt,
        next_at = d.Status == "failed" ? d.NextAt : (DateTime?)null,
    };

    /// <summary>A plain name for a phone from its browser, so the owner can tell two apart.</summary>
    private static string DeviceName(string userAgent)
    {
        var os = userAgent.Contains("iPhone", StringComparison.Ordinal) ? "iPhone"
            : userAgent.Contains("iPad", StringComparison.Ordinal) ? "iPad"
            : userAgent.Contains("Android", StringComparison.Ordinal) ? "Android"
            : userAgent.Contains("Mac OS", StringComparison.Ordinal) ? "Mac"
            : userAgent.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : userAgent.Contains("Linux", StringComparison.Ordinal) ? "Linux" : "A device";
        var browser = userAgent.Contains("Firefox", StringComparison.Ordinal) ? "Firefox"
            : userAgent.Contains("Edg/", StringComparison.Ordinal) ? "Edge"
            : userAgent.Contains("Chrome", StringComparison.Ordinal) ? "Chrome"
            : userAgent.Contains("Safari", StringComparison.Ordinal) ? "Safari" : null;
        return browser is null ? os : $"{os}, {browser}";
    }

    private static Actor Owner(HttpContext ctx, Guid blogId) => Actor.Owner(blogId, SignIn.Current(ctx).AccountId);

    private static IResult NotFound() => SignIn.Problem(404, "not_found", "No such blog of yours.");

    private static Task<bool> OwnsAsync(Db db, HttpContext ctx, Guid blogId) => db.ReadAsync(tx => tx.One<bool>("""
        SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @blogId AND account_id = @accountId AND role = 'owner')
        """, new { blogId, accountId = SignIn.Current(ctx).AccountId }), ctx.RequestAborted);
}
