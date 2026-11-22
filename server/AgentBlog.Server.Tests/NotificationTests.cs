using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentBlog.Notify;
using AgentKit.Channels;
using AgentKit.Push;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>Where notifications go, as the server sees it: every request recorded, answers scripted per URL (200 when none).</summary>
internal sealed class StubOutside : HttpMessageHandler
{
    public sealed record Received(string Url, Dictionary<string, string> Headers, byte[] Body)
    {
        public JsonNode Json => JsonNode.Parse(Body)!;
    }

    private readonly ConcurrentQueue<Received> _received = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<HttpResponseMessage>>> _script = new();

    public IReadOnlyList<Received> At(string urlStart) => [.. _received.Where(r => r.Url.StartsWith(urlStart, StringComparison.Ordinal))];

    public int Count => _received.Count;

    public void Answer(string urlStart, HttpStatusCode status, string? retryAfter = null)
    {
        _script.GetOrAdd(urlStart, _ => new()).Enqueue(() =>
        {
            var r = new HttpResponseMessage(status) { Content = new StringContent("""{"message": "scripted"}""") };
            if (retryAfter is not null) r.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return r;
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        if (request.Content is not null)
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var url = request.RequestUri!.AbsoluteUri;
        _received.Enqueue(new Received(url, headers, body));
        foreach (var (start, queue) in _script)
            if (url.StartsWith(start, StringComparison.Ordinal) && queue.TryDequeue(out var answer)) return answer();
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

/// <summary>A phone that turned push on: its own key pair and secret; it decrypts what arrives as a browser does.</summary>
internal sealed class TestPhone : IDisposable
{
    private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    public byte[] Auth { get; } = RandomNumberGenerator.GetBytes(16);
    public string Endpoint { get; } = $"https://push.example.net/send/{Guid.NewGuid():N}";

    public byte[] PublicKey
    {
        get
        {
            var q = _key.ExportParameters(false).Q;
            return [0x04, .. q.X!, .. q.Y!];
        }
    }

    public object Subscription(string? label = null) => new
    {
        endpoint = Endpoint,
        keys = new { p256dh = Base64Url.EncodeToString(PublicKey), auth = Base64Url.EncodeToString(Auth) },
        label,
    };

    public JsonNode Read(StubOutside.Received r) => JsonNode.Parse(WebPushReceiver.Decrypt(r.Body, _key, PublicKey, Auth))!;

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// M2 slice 3 (docs/specs/notifications.md): the owner's channels get what needs them, judged when sent, escaped,
/// signed, retried; a channel that is gone stops with the reason, and the owner's other channels say so.
/// </summary>
public sealed class NotificationTests
{
    private const string MasterKey = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string DiscordUrl = "https://discord.com/api/webhooks/123456/secret-token-abcd";
    private const string SlackUrl = "https://hooks.slack.com/services/T0/B0/slack-secret";
    private const string HookUrl = "https://hooks.example.com/blog";

    private sealed class Notified : IAsyncDisposable
    {
        public required PostTests.World W { get; init; }
        public required StubOutside Outside { get; init; }
        public required string Blog { get; init; }

        public static async Task<Notified> StartAsync(bool secrets = true)
        {
            var outside = new StubOutside();
            var w = await PostTests.World.StartAsync(b =>
            {
                if (secrets) b.UseSetting("Blog:Secrets:MasterKey", MasterKey);
                b.UseSetting("Blog:Notify:IntervalSeconds", "0"); // the tests run each pass themselves
                b.ConfigureTestServices(s => s.AddHttpClient(Notifier.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => outside));
            });
            var blog = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
            return new Notified { W = w, Outside = outside, Blog = blog };
        }

        public Task<Notifier.Pass> PassAsync() => W.Host.App.Factory.Services.GetRequiredService<Notifier>().RunOnceAsync(default);

        public async Task<JsonResult> AddAsync(object body) => await W.Owner.PostJsonAsync($"/api/blogs/{Blog}/channels", body, csrf: true);

        public async Task<string> AddOkAsync(object body)
        {
            var r = await AddAsync(body);
            Assert.True(r.Status == HttpStatusCode.OK, r.Json?.ToJsonString());
            return r.Json!["channel"]!["id"]!.GetValue<string>();
        }

        public async Task<JsonObject> ScreenAsync() => (await W.Owner.GetJsonAsync($"/api/blogs/{Blog}/notifications")).Json!.AsObject();

        public async Task<JsonNode> ChannelAsync(string id) =>
            (await ScreenAsync())["channels"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == id)!;

        /// <summary>Everything waiting to be retried is due now (the tests don't wait out the schedule).</summary>
        public async Task DueNowAsync()
        {
            await using var conn = new NpgsqlConnection(W.Host.App.OwnerConnectionString);
            await conn.ExecuteAsync("UPDATE delivery SET next_at = now() - interval '1 second' WHERE status IN ('pending', 'failed')");
        }

        public async Task<string> AskToPublishAsync(string title = "Launch notes")
        {
            var draft = await W.DraftAsync(title, "Body.");
            var ask = await W.OkAsync("publish_post", new { post = draft["id"]!.GetValue<string>() });
            return ask["request"]!["id"]!.GetValue<string>();
        }

        public ValueTask DisposeAsync() => W.DisposeAsync();
    }

    [Fact]
    public async Task Without_a_master_key_notifications_say_what_to_set_and_nothing_else_breaks()
    {
        await using var n = await Notified.StartAsync(secrets: false);
        var screen = await n.ScreenAsync();
        Assert.False(screen["available"]!.GetValue<bool>());
        Assert.Contains("Blog__Secrets__MasterKey", screen["reason"]!.GetValue<string>());
        var add = await n.AddAsync(new { kind = "discord", label = "Mine", url = DiscordUrl });
        Assert.Equal(HttpStatusCode.Conflict, add.Status);
        Assert.Equal("secrets_off", add.Json!["code"]?.GetValue<string>() ?? add.Json!["error"]?.GetValue<string>());
        await n.AskToPublishAsync(); // the rest of the blog runs
        Assert.Equal(0, (await n.PassAsync()).Sent);
    }

    [Fact]
    public async Task An_agents_request_reaches_discord_once_with_its_text_escaped_and_mentioning_nobody()
    {
        await using var n = await Notified.StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await n.AddAsync(new { kind = "discord", label = "Mine", url = "https://evil.example/api/webhooks/1/t" })).Status);
        var id = await n.AddOkAsync(new { kind = "discord", label = "My server", url = DiscordUrl });

        await n.AskToPublishAsync("@everyone [free](https://evil.example)");
        Assert.Equal(1, (await n.PassAsync()).Sent);
        var got = Assert.Single(n.Outside.At("https://discord.com/api/webhooks/123456/"));
        Assert.EndsWith("?wait=true", got.Url);
        Assert.Empty(got.Json["allowed_mentions"]!["parse"]!.AsArray());
        var embed = got.Json["embeds"]![0]!;
        Assert.Equal("Claude asks to publish “@everyone [free](https://evil.example)”", embed["title"]!.GetValue<string>());
        Assert.Equal("https://blog.test/admin/#/", embed["url"]!.GetValue<string>());

        Assert.Equal(0, (await n.PassAsync()).Sent); // once
        await using (var owner = new NpgsqlConnection(n.W.Host.App.OwnerConnectionString))
            Assert.Equal(await owner.QuerySingleAsync<long>("SELECT max(id) FROM event"),
                await owner.QuerySingleAsync<long>("SELECT last_event_id FROM notify_cursor WHERE name = 'notifier'"));
        var channel = await n.ChannelAsync(id);
        Assert.Equal("sent", channel["recent"]![0]!["status"]!.GetValue<string>());
        Assert.Equal("approval_requested", channel["recent"]![0]!["event"]!.GetValue<string>());

        // The webhook's token never comes back, and isn't in the database in the clear.
        var screen = (await n.ScreenAsync()).ToJsonString();
        Assert.DoesNotContain("secret-token", screen);
        Assert.Contains("discord.com", channel["hint"]!.GetValue<string>());
        await using var conn = new NpgsqlConnection(n.W.Host.App.OwnerConnectionString);
        var stored = await conn.QuerySingleAsync<byte[]>("SELECT ciphertext FROM channel_secret WHERE name = 'url'");
        Assert.DoesNotContain("secret-token", Encoding.UTF8.GetString(stored));
    }

    [Fact]
    public async Task A_message_is_judged_when_it_is_sent_and_the_owners_own_doings_are_never_sent_back()
    {
        await using var n = await Notified.StartAsync();
        var id = await n.AddOkAsync(new { kind = "slack", label = "Team", url = SlackUrl, events = new[] { "approval_requested", "post_published" } });

        // Decided before the worker came round: skipped, with the reason, never sent.
        var request = await n.AskToPublishAsync();
        Assert.Equal(HttpStatusCode.OK, (await n.W.OwnerPostAsync($"/api/requests/{request}/approve")).Status);
        var pass = await n.PassAsync();
        Assert.Equal(0, pass.Sent);
        Assert.Equal(1, pass.Skipped);
        var skipped = (await n.ChannelAsync(id))["recent"]![0]!;
        Assert.Equal("skipped", skipped["status"]!.GetValue<string>());
        Assert.Contains("approved", skipped["error"]!.GetValue<string>());
        Assert.Empty(n.Outside.At("https://hooks.slack.com/"));

        // The owner approving published the post: their own act, not sent back to them. An agent publishing is.
        await n.W.SetLevelAsync("publish");
        var draft = await n.W.DraftAsync("By the agent", "Body.");
        await n.W.OkAsync("publish_post", new { post = draft["id"]!.GetValue<string>() });
        Assert.Equal(1, (await n.PassAsync()).Sent);
        var got = Assert.Single(n.Outside.At("https://hooks.slack.com/"));
        Assert.Contains("“By the agent” is live", got.Json["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_channel_that_is_gone_stops_with_the_reason_and_the_other_channels_say_so()
    {
        await using var n = await Notified.StartAsync();
        var discord = await n.AddOkAsync(new { kind = "discord", label = "Old server", url = DiscordUrl });
        await n.AddOkAsync(new { kind = "slack", label = "Team", url = SlackUrl });
        n.Outside.Answer("https://discord.com/", HttpStatusCode.NotFound);

        await n.AskToPublishAsync();
        await n.PassAsync();
        var channel = await n.ChannelAsync(discord);
        Assert.False(channel["enabled"]!.GetValue<bool>());
        Assert.Contains("404", channel["disabled_reason"]!.GetValue<string>());
        Assert.Contains(("channel_disabled", "system"), await n.W.EventsAsync());

        // Turned back on before the next pass: that clears why it stopped, and it still isn't told about its own stop.
        var on = await n.W.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{n.Blog}/channels/{discord}", csrf: true, body: new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, on.Status);
        Assert.Null(on.Json!["channel"]!["disabled_reason"]);
        Assert.True(on.Json!["channel"]!["enabled"]!.GetValue<bool>());

        // The next pass carries the notice to Slack, and not to the channel that stopped.
        await n.PassAsync();
        Assert.Contains(n.Outside.At("https://hooks.slack.com/"), r => r.Json["text"]!.GetValue<string>() == "Notifications to Old server stopped");
        Assert.Single(n.Outside.At("https://discord.com/"));
    }

    [Fact]
    public async Task A_failure_is_retried_on_the_schedule_or_the_destinations_retry_after_and_then_clears()
    {
        await using var n = await Notified.StartAsync();
        var id = await n.AddOkAsync(new { kind = "slack", label = "Team", url = SlackUrl });
        n.Outside.Answer("https://hooks.slack.com/", HttpStatusCode.TooManyRequests, retryAfter: "600");
        n.Outside.Answer("https://hooks.slack.com/", HttpStatusCode.TooManyRequests, retryAfter: "120");

        await n.AskToPublishAsync();
        var before = DateTime.UtcNow;
        Assert.Equal(1, (await n.PassAsync()).Failed);
        var first = await n.ChannelAsync(id);
        Assert.NotNull(first["failing_since"]);
        Assert.Contains("429", first["last_error"]!.GetValue<string>());
        var next = first["recent"]![0]!["next_at"]!.GetValue<DateTime>();
        Assert.InRange(next, before.AddSeconds(590), before.AddMinutes(11)); // Slack's 10 minutes beat the schedule's 1

        await n.DueNowAsync();
        before = DateTime.UtcNow;
        Assert.Equal(1, (await n.PassAsync()).Failed);
        next = (await n.ChannelAsync(id))["recent"]![0]!["next_at"]!.GetValue<DateTime>();
        Assert.InRange(next, before.AddSeconds(290), before.AddMinutes(6)); // the schedule's 5 minutes beat Slack's 2

        await n.DueNowAsync();
        Assert.Equal(1, (await n.PassAsync()).Sent);
        var cleared = await n.ChannelAsync(id);
        Assert.Null(cleared["failing_since"]);
        Assert.Null(cleared["last_error"]);
        Assert.Equal(3, cleared["recent"]![0]!["attempts"]!.GetValue<int>());
        Assert.Equal(3, n.Outside.At("https://hooks.slack.com/").Count);
    }

    [Fact]
    public async Task A_webhook_is_signed_with_a_secret_shown_once_and_keeps_its_id_across_retries()
    {
        await using var n = await Notified.StartAsync();
        var add = await n.AddAsync(new { kind = "webhook", label = "n8n", url = HookUrl });
        var secret = add.Json!["signing_secret"]!.GetValue<string>();
        Assert.StartsWith("whsec_", secret);
        Assert.DoesNotContain(secret, (await n.ScreenAsync()).ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, (await n.AddAsync(new { kind = "webhook", label = "x", url = "http://hooks.example.com/" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await n.AddAsync(new { kind = "webhook", label = "x", url = "https://127.0.0.1/hook" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await n.AddAsync(new { kind = "webhook", label = "x", url = "https://localhost/hook" })).Status);

        n.Outside.Answer(HookUrl, HttpStatusCode.InternalServerError);
        await n.AskToPublishAsync();
        await n.PassAsync();
        await n.DueNowAsync();
        await n.PassAsync();
        var both = n.Outside.At(HookUrl);
        Assert.Equal(2, both.Count);
        Assert.Equal(both[0].Headers["webhook-id"], both[1].Headers["webhook-id"]);
        var last = both[1];
        Assert.True(StandardWebhooks.Verify(secret, last.Headers["webhook-id"], last.Headers["webhook-timestamp"], last.Headers["webhook-signature"],
            last.Body, DateTimeOffset.UtcNow));
        Assert.Equal("approval_requested", last.Json["type"]!.GetValue<string>());
        Assert.Equal("publish", last.Json["data"]!["action"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ntfy_on_a_public_topic_says_what_happened_without_the_title_unless_asked()
    {
        await using var n = await Notified.StartAsync();
        var open = await n.AddOkAsync(new { kind = "ntfy", label = "Phone", topic = "blog-7f3a9c" });
        Assert.Equal("minimal", (await n.ChannelAsync(open))["detail"]!.GetValue<string>());
        await n.AddOkAsync(new { kind = "ntfy", label = "Private", server = "https://ntfy.example.com", topic = "mine", token = "tk_abc" });
        Assert.Equal(HttpStatusCode.BadRequest, (await n.AddAsync(new { kind = "ntfy", label = "x", topic = "has space" })).Status);

        await n.AskToPublishAsync("Secret draft title");
        await n.PassAsync();
        var minimal = Assert.Single(n.Outside.At("https://ntfy.sh/"));
        Assert.Equal("blog-7f3a9c", minimal.Json["topic"]!.GetValue<string>());
        Assert.DoesNotContain("Secret draft", minimal.Json.ToJsonString());
        Assert.Equal("Claude asks to publish a post", minimal.Json["title"]!.GetValue<string>());
        var full = Assert.Single(n.Outside.At("https://ntfy.example.com/"));
        Assert.Equal("Bearer tk_abc", full.Headers["Authorization"]);
        Assert.Contains("Secret draft title", full.Json["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_phone_gets_an_encrypted_signed_push_and_a_phone_that_is_gone_is_let_go()
    {
        await using var n = await Notified.StartAsync();
        using var phone = new TestPhone();
        using var other = new TestPhone();
        var vapid = (await n.ScreenAsync())["vapid_public_key"]!.GetValue<string>();
        Assert.Equal(65, Base64Url.DecodeFromChars(vapid).Length);

        Assert.Equal(HttpStatusCode.OK, (await n.W.Owner.PostJsonAsync($"/api/blogs/{n.Blog}/push/devices", phone.Subscription("Pixel"), csrf: true)).Status);
        Assert.Equal(HttpStatusCode.OK, (await n.W.Owner.PostJsonAsync($"/api/blogs/{n.Blog}/push/devices", other.Subscription(), csrf: true)).Status);
        foreach (var bad in new object[]
        {
            new { endpoint = "http://push.example.net/x", keys = new { p256dh = Base64Url.EncodeToString(phone.PublicKey), auth = Base64Url.EncodeToString(phone.Auth) } },
            new { endpoint = "https://10.0.0.5/x", keys = new { p256dh = Base64Url.EncodeToString(phone.PublicKey), auth = Base64Url.EncodeToString(phone.Auth) } },
            new { endpoint = "https://push.example.net/x", keys = new { p256dh = Base64Url.EncodeToString(new byte[65]), auth = Base64Url.EncodeToString(phone.Auth) } },
            new { endpoint = "https://push.example.net/x", keys = new { p256dh = Base64Url.EncodeToString(phone.PublicKey), auth = "AA" } },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await n.W.Owner.PostJsonAsync($"/api/blogs/{n.Blog}/push/devices", bad, csrf: true)).Status);

        n.Outside.Answer(other.Endpoint, HttpStatusCode.Gone);
        await n.AskToPublishAsync("Hello phones");
        await n.PassAsync();
        var got = Assert.Single(n.Outside.At(phone.Endpoint));
        var message = phone.Read(got);
        Assert.Equal("Claude asks to publish “Hello phones”", message["title"]!.GetValue<string>());
        Assert.Equal("https://blog.test/admin/#/", message["url"]!.GetValue<string>());
        Assert.Equal("aes128gcm", got.Headers["Content-Encoding"]);
        Assert.Equal("high", got.Headers["Urgency"]);
        var (k, _) = WebPushReceiver.VerifyAuthorization(got.Headers["Authorization"], new Uri(phone.Endpoint), DateTimeOffset.UtcNow);
        Assert.Equal(vapid, k);

        var phones = (await n.ScreenAsync())["channels"]!.AsArray().Single(c => c!["kind"]!.GetValue<string>() == "webpush")!;
        Assert.Equal("Pixel", Assert.Single(phones["devices"]!.AsArray())!["label"]!.GetValue<string>()); // the gone one was let go
        Assert.True(phones["enabled"]!.GetValue<bool>()); // one phone gone doesn't stop the others
        Assert.Contains(("push_device_gone", "system"), await n.W.EventsAsync());

        // The owner turns this phone off: nothing more goes to it.
        Assert.Equal(HttpStatusCode.NoContent, (await n.W.Owner.PostJsonAsync($"/api/blogs/{n.Blog}/push/devices/remove", new { endpoint = phone.Endpoint }, csrf: true)).Status);
        await n.AskToPublishAsync("Again");
        await n.PassAsync();
        Assert.Single(n.Outside.At(phone.Endpoint));
    }

    [Fact]
    public async Task Send_test_shows_the_owner_a_channel_working_or_why_not()
    {
        await using var n = await Notified.StartAsync();
        var ok = await n.AddOkAsync(new { kind = "discord", label = "Mine", url = DiscordUrl });
        var test = await n.W.Owner.PostJsonAsync($"/api/blogs/{n.Blog}/channels/{ok}/test", null, csrf: true);
        Assert.Equal("sent", test.Json!["deliveries"]![0]!["status"]!.GetValue<string>());
        Assert.Equal("Test from " + (await n.W.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["title"]!.GetValue<string>(),
            Assert.Single(n.Outside.At("https://discord.com/")).Json["embeds"]![0]!["title"]!.GetValue<string>());

        var broken = await n.AddOkAsync(new { kind = "slack", label = "Broken", url = SlackUrl });
        n.Outside.Answer("https://hooks.slack.com/", HttpStatusCode.Forbidden);
        var failed = await n.W.Owner.PostJsonAsync($"/api/blogs/{n.Blog}/channels/{broken}/test", null, csrf: true);
        Assert.Equal("dead", failed.Json!["deliveries"]![0]!["status"]!.GetValue<string>());
        Assert.Contains("403", failed.Json!["deliveries"]![0]!["error"]!.GetValue<string>());
        Assert.False((await n.ChannelAsync(broken))["enabled"]!.GetValue<bool>());

        // No phones yet: the push test says so instead of pretending.
        var phones = await n.W.Owner.PostJsonAsync($"/api/blogs/{n.Blog}/channels/{Guid.NewGuid()}/test", null, csrf: true);
        Assert.Equal(HttpStatusCode.NotFound, phones.Status);
    }

    [Fact]
    public async Task Removing_a_channel_deletes_its_secrets_and_what_waited_is_skipped()
    {
        await using var n = await Notified.StartAsync();
        var id = await n.AddOkAsync(new { kind = "discord", label = "Mine", url = DiscordUrl });
        n.Outside.Answer("https://discord.com/", HttpStatusCode.ServiceUnavailable);
        await n.AskToPublishAsync();
        await n.PassAsync(); // failed, waiting for a retry
        Assert.Equal(HttpStatusCode.NoContent, (await n.W.Owner.SendAsync(HttpMethod.Delete, $"/api/blogs/{n.Blog}/channels/{id}", csrf: true)).Status);
        Assert.Empty((await n.ScreenAsync())["channels"]!.AsArray());
        await using var conn = new NpgsqlConnection(n.W.Host.App.OwnerConnectionString);
        Assert.Equal(0, await conn.QuerySingleAsync<int>("SELECT count(*) FROM channel_secret"));
        Assert.Equal("skipped", await conn.QuerySingleAsync<string>("SELECT status FROM delivery"));
        await n.DueNowAsync();
        await n.PassAsync();
        Assert.Single(n.Outside.At("https://discord.com/"));
        Assert.Contains(("channel_removed", $"account:{await conn.QuerySingleAsync<Guid>("SELECT account_id FROM blog_member LIMIT 1")}"), await n.W.EventsAsync());
    }

    [Fact]
    public async Task Choices_are_checked_and_only_the_owner_reaches_notifications()
    {
        await using var n = await Notified.StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await n.AddAsync(new { kind = "discord", label = "x", url = DiscordUrl, events = new[] { "post_deleted" } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await n.AddAsync(new { kind = "discord", label = "x", url = DiscordUrl, events = Array.Empty<string>() })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await n.AddAsync(new { kind = "discord", label = " ", url = DiscordUrl })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await n.AddAsync(new { kind = "email", label = "x" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await n.AddAsync(new { kind = "webpush", label = "x" })).Status);
        var id = await n.AddOkAsync(new { kind = "discord", label = "x", url = DiscordUrl });
        Assert.Equal(HttpStatusCode.BadRequest, (await n.W.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{n.Blog}/channels/{id}", csrf: true,
            body: new { url = "https://discord.com/api/webhooks/9/other" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await n.W.Owner.PostJsonAsync($"/api/blogs/{n.Blog}/channels", new { kind = "discord", label = "x", url = DiscordUrl }, csrf: false)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await n.W.Host.Browser().GetJsonAsync($"/api/blogs/{n.Blog}/notifications")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await n.W.Owner.GetJsonAsync($"/api/blogs/{Guid.NewGuid()}/notifications")).Status);
        var catalog = (await n.ScreenAsync())["events"]!.AsArray();
        Assert.Contains(catalog, e => e!["kind"]!.GetValue<string>() == "post_published" && !e["default"]!.GetValue<bool>());
    }
}
