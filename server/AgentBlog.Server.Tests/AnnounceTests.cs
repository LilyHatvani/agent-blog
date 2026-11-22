using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentBlog.Announce;
using AgentBlog.Posts;
using AgentKit.Social;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>The social networks as AgentKit.Social reaches them: every request recorded, answered as each network does.</summary>
internal sealed class SocialStub : HttpMessageHandler
{
    public sealed record Received(string Url, string Body)
    {
        public JsonNode Json => JsonNode.Parse(Body)!;
    }

    private readonly ConcurrentQueue<Received> _received = new();

    public IReadOnlyList<Received> At(string urlStart) => [.. _received.Where(r => r.Url.StartsWith(urlStart, StringComparison.Ordinal))];

    /// <summary>When it answers, that answer is used instead of the network's usual one.</summary>
    public Func<Received, HttpResponseMessage?>? Answer { get; set; }

    /// <summary>When it gives an exception, the request fails with it (a connection that broke before the answer).</summary>
    public Func<Received, Exception?>? Fail { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var r = new Received(request.RequestUri!.AbsoluteUri, body);
        _received.Enqueue(r);
        if (Fail?.Invoke(r) is { } e) throw e;
        if (Answer?.Invoke(r) is { } answer) return answer;
        return request.RequestUri.Host switch
        {
            "discord.com" when request.Method == HttpMethod.Post => Json("""{"id":"9001","channel_id":"77","guild_id":"55"}"""),
            "hooks.slack.com" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") },
            "api.telegram.org" => Json("""{"ok":true,"result":{"message_id":5,"chat":{"id":-1001234,"type":"channel","username":"blogchan"}}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>
/// M2 slice 7 (docs/specs/announce.md): a new post goes to the owner's accounts after the wait, judged when it is sent;
/// what an agent published on its own, or wrote, waits for the owner's tap; nothing posts twice.
/// </summary>
public sealed class AnnounceTests
{
    private const string MasterKey = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string DiscordUrl = "https://discord.com/api/webhooks/123456/announce-secret-token";
    private const string SlackUrl = "https://hooks.slack.com/services/T0/B0/announce-slack-secret";

    private sealed class Announcing : IAsyncDisposable
    {
        public required PostTests.World W { get; init; }
        public required SocialStub Networks { get; init; }
        public required string Blog { get; init; }
        public required StubOutside Notified { get; init; }

        public static async Task<Announcing> StartAsync(bool secrets = true)
        {
            var networks = new SocialStub();
            var notified = new StubOutside();
            var w = await PostTests.World.StartAsync(b =>
            {
                if (secrets) b.UseSetting("Blog:Secrets:MasterKey", MasterKey);
                b.ConfigureTestServices(s =>
                {
                    s.AddHttpClient(SocialDefaults.ApiHttpClientName).ConfigurePrimaryHttpMessageHandler(() => networks);
                    s.AddHttpClient(SocialDefaults.MediaHttpClientName).ConfigurePrimaryHttpMessageHandler(() => networks);
                    s.AddHttpClient(SocialAccounts.ProbeHttpClientName).ConfigurePrimaryHttpMessageHandler(() => networks);
                    s.AddHttpClient(AgentBlog.Notify.Notifier.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => notified);
                });
            });
            var blog = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
            return new Announcing { W = w, Networks = networks, Blog = blog, Notified = notified };
        }

        private Announcer Announcer => W.Host.App.Factory.Services.GetRequiredService<Announcer>();

        /// <summary>The worker's two passes at <paramref name="minutes"/> from now (the tests don't wait out the delay).</summary>
        public async Task<int> PassAsync(int minutes = 0)
        {
            await Announcer.CreatePassAsync(default);
            return (await Announcer.SendPassAsync(DateTimeOffset.UtcNow.AddMinutes(minutes), default)).Count;
        }

        public async Task<JsonResult> AddAsync(object body) => await W.Owner.PostJsonAsync($"/api/blogs/{Blog}/social-accounts", body, csrf: true);

        public async Task<string> AddOkAsync(object body)
        {
            var r = await AddAsync(body);
            Assert.True(r.Status == HttpStatusCode.Created, r.Json?.ToJsonString());
            return r.Json!["account"]!["id"]!.GetValue<string>();
        }

        /// <summary>A post the agent drafted and the owner published.</summary>
        public async Task<string> OwnerPublishesAsync(string title = "Launch notes", string body = "Body of the post.")
        {
            var draft = await W.DraftAsync(title, body);
            var id = draft["id"]!.GetValue<string>();
            Assert.Equal(HttpStatusCode.OK, (await W.OwnerPostAsync($"/api/posts/{id}/publish")).Status);
            return id;
        }

        public async Task<JsonNode> AnnouncementAsync(string post, string account) =>
            (await W.Owner.GetJsonAsync($"/api/posts/{post}/announcements")).Json!["announcements"]!.AsArray()
                .Single(a => a!["account"]!["id"]!.GetValue<string>() == account)!;

        public Task<JsonResult> ActAsync(string post, string account, string action) =>
            W.OwnerPostAsync($"/api/posts/{post}/announcements/{account}/{action}");

        public async Task<T> SqlAsync<T>(string sql, object? args = null)
        {
            await using var conn = new NpgsqlConnection(W.Host.App.OwnerConnectionString);
            return await conn.QuerySingleAsync<T>(sql, args);
        }

        public ValueTask DisposeAsync() => W.DisposeAsync();
    }

    [Fact]
    public async Task A_post_the_owner_published_goes_out_once_after_the_wait_with_its_card_and_a_plain_link()
    {
        await using var a = await Announcing.StartAsync();
        var discord = await a.AddOkAsync(new { network = "discord", url = DiscordUrl });
        var post = await a.OwnerPublishesAsync("Launch notes");

        Assert.Equal(0, await a.PassAsync()); // the wait (10 minutes) hasn't passed
        var waiting = await a.AnnouncementAsync(post, discord);
        Assert.Equal("scheduled", waiting["state"]!.GetValue<string>());
        Assert.Empty(a.Networks.At("https://discord.com/"));

        Assert.Equal(1, await a.PassAsync(minutes: 11));
        var sent = Assert.Single(a.Networks.At("https://discord.com/api/v10/webhooks/123456/"));
        var embed = sent.Json["embeds"]![0]!;
        Assert.Equal("Launch notes", embed["title"]!.GetValue<string>());
        // The post's own address: nothing added to it.
        Assert.Equal("https://blog.test/posts/launch-notes", embed["url"]!.GetValue<string>());
        Assert.StartsWith("https://blog.test/cards/posts/launch-notes.png?v=", embed["image"]!["url"]!.GetValue<string>());
        Assert.DoesNotContain("utm_", sent.Body);

        var posted = await a.AnnouncementAsync(post, discord);
        Assert.Equal("posted", posted["state"]!.GetValue<string>());
        Assert.Equal("https://discord.com/channels/55/77/9001", posted["url"]!.GetValue<string>());

        // Taken down and put back: a second publication isn't announced, not even to an account added in between.
        Assert.Equal(HttpStatusCode.OK, (await a.W.OwnerPostAsync($"/api/posts/{post}/unpublish")).Status);
        var slack = await a.AddOkAsync(new { network = "slack", url = SlackUrl });
        Assert.Equal(HttpStatusCode.OK, (await a.W.OwnerPostAsync($"/api/posts/{post}/publish")).Status);
        Assert.Equal(0, await a.PassAsync(minutes: 30));
        Assert.Single(a.Networks.At("https://discord.com/api/v10/webhooks/"));
        Assert.Equal("none", (await a.AnnouncementAsync(post, slack))["state"]!.GetValue<string>());
        Assert.Contains(await a.W.EventsAsync(), e => e.Kind == "announcement_posted");
    }

    [Fact]
    public async Task A_post_an_agent_published_on_its_own_waits_for_the_owners_tap()
    {
        await using var a = await Announcing.StartAsync();
        var slack = await a.AddOkAsync(new { network = "slack", url = SlackUrl });
        await a.W.SetLevelAsync("publish");
        var draft = await a.W.DraftAsync("By myself", "Body.");
        var post = draft["id"]!.GetValue<string>();
        Assert.Equal("done", (await a.W.OkAsync("publish_post", new { post }))["status"]!.GetValue<string>());

        Assert.Equal(0, await a.PassAsync(minutes: 60));
        var row = await a.AnnouncementAsync(post, slack);
        Assert.Equal("waiting", row["state"]!.GetValue<string>());
        Assert.Equal("agent_published", row["why_waiting"]!.GetValue<string>());
        Assert.Empty(a.Networks.At("https://hooks.slack.com/"));
        Assert.Contains(await a.W.EventsAsync(), e => e.Kind == "announcement_waiting");

        // The agent has no way to send it: writing text keeps it waiting.
        Assert.Equal("waiting", (await a.W.OkAsync("write_announcement", new { post, text = "Out now." }))["announcements"]![0]!["state"]!.GetValue<string>());
        Assert.Equal(0, await a.PassAsync(minutes: 60));

        Assert.Equal(HttpStatusCode.OK, (await a.ActAsync(post, slack, "send")).Status);
        Assert.Equal(1, await a.PassAsync());
        var sent = Assert.Single(a.Networks.At("https://hooks.slack.com/"));
        Assert.StartsWith("Out now.", sent.Json["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_schedule_an_agent_set_waits_too_and_one_the_owner_set_does_not()
    {
        Assert.True(Announcer.OwnerVouched("account:" + Guid.NewGuid(), null));
        Assert.True(Announcer.OwnerVouched("system", "account:" + Guid.NewGuid()));
        Assert.False(Announcer.OwnerVouched("system", "connection:" + Guid.NewGuid()));
        Assert.False(Announcer.OwnerVouched("connection:" + Guid.NewGuid(), null));
        Assert.False(Announcer.OwnerVouched("system", null));

        await using var a = await Announcing.StartAsync();
        var slack = await a.AddOkAsync(new { network = "slack", url = SlackUrl });
        await a.W.SetLevelAsync("publish");
        var draft = await a.W.DraftAsync("Later", "Body.");
        var post = draft["id"]!.GetValue<string>();
        var at = DateTimeOffset.UtcNow.AddMinutes(5);
        Assert.Equal("done", (await a.W.OkAsync("schedule_post", new { post, at = at.ToString("O") }))["status"]!.GetValue<string>());
        await a.W.Host.App.Factory.Services.GetRequiredService<Scheduler>().RunOnceAsync(at.AddMinutes(1), default);
        await a.PassAsync(minutes: 60);
        Assert.Equal("waiting", (await a.AnnouncementAsync(post, slack))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Words_an_agent_wrote_wait_for_the_owner_even_on_a_post_the_owner_published()
    {
        await using var a = await Announcing.StartAsync();
        var discord = await a.AddOkAsync(new { network = "discord", url = DiscordUrl });
        var slack = await a.AddOkAsync(new { network = "slack", url = SlackUrl });

        // Before it is live: a draft for one account.
        var draft = await a.W.DraftAsync("Written ahead", "Body.");
        var post = draft["id"]!.GetValue<string>();
        var wrote = await a.W.OkAsync("write_announcement", new { post, text = "My words for Slack.", account = slack });
        Assert.Equal("draft", wrote["announcements"]![0]!["state"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await a.W.OwnerPostAsync($"/api/posts/{post}/publish")).Status);

        await a.PassAsync(minutes: 11);
        // The account without agent words went out; the one with them waits.
        Assert.Single(a.Networks.At("https://discord.com/api/v10/webhooks/"));
        Assert.Empty(a.Networks.At("https://hooks.slack.com/"));
        var row = await a.AnnouncementAsync(post, slack);
        Assert.Equal("waiting", row["state"]!.GetValue<string>());
        Assert.Equal("agent_text", row["why_waiting"]!.GetValue<string>());

        // And on a scheduled one: an agent's words stop it.
        var second = await a.OwnerPublishesAsync("Second");
        await a.PassAsync();
        Assert.Equal("scheduled", (await a.AnnouncementAsync(second, discord))["state"]!.GetValue<string>());
        await a.W.OkAsync("write_announcement", new { post = second, text = "Agent says hi.", account = discord });
        Assert.Equal("waiting", (await a.AnnouncementAsync(second, discord))["state"]!.GetValue<string>());
        await a.PassAsync(minutes: 60); // the second post's Slack announcement goes; its Discord one doesn't
        Assert.Single(a.Networks.At("https://discord.com/api/v10/webhooks/"));
        Assert.Equal("waiting", (await a.AnnouncementAsync(second, discord))["state"]!.GetValue<string>());

        // An agent can't set it back to the default to slip past the tap.
        Assert.True((await a.W.ToolAsync("write_announcement", new { post = second, text = "", account = discord })).Refused);

        Assert.Equal(HttpStatusCode.OK, (await a.ActAsync(post, slack, "send")).Status);
        await a.PassAsync();
        Assert.Contains(a.Networks.At("https://hooks.slack.com/"), r => r.Json["text"]!.GetValue<string>().StartsWith("My words for Slack.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_owner_is_told_what_waits_for_them_unless_it_stopped_waiting_first()
    {
        await using var a = await Announcing.StartAsync();
        var slack = await a.AddOkAsync(new { network = "slack", url = SlackUrl, label = "Team Slack" });
        var notify = await a.W.Owner.PostJsonAsync($"/api/blogs/{a.Blog}/channels",
            new { kind = "discord", label = "Me", url = "https://discord.com/api/webhooks/42/notify-token", events = new[] { "announcement_waiting" } }, csrf: true);
        Assert.Equal(HttpStatusCode.OK, notify.Status);
        var notifier = a.W.Host.App.Factory.Services.GetRequiredService<AgentBlog.Notify.Notifier>();
        await notifier.RunOnceAsync(default); // what the setup itself said

        await a.W.SetLevelAsync("publish");
        var first = (await a.W.DraftAsync("Agent post", "Body."))["id"]!.GetValue<string>();
        await a.W.OkAsync("publish_post", new { post = first });
        await a.PassAsync();
        await notifier.RunOnceAsync(default);
        var told = Assert.Single(a.Notified.At("https://discord.com/api/webhooks/42/"));
        Assert.Equal("“Agent post” waits to be announced on Team Slack", told.Json["embeds"]![0]!["title"]!.GetValue<string>());

        // Sent before the notifier came round: judged when it is sent, so it isn't.
        var second = (await a.W.DraftAsync("Second agent post", "Body."))["id"]!.GetValue<string>();
        await a.W.OkAsync("publish_post", new { post = second });
        await a.PassAsync();
        Assert.Equal(HttpStatusCode.OK, (await a.ActAsync(second, slack, "send")).Status);
        await notifier.RunOnceAsync(default);
        Assert.Single(a.Notified.At("https://discord.com/api/webhooks/42/"));
    }

    [Fact]
    public async Task A_post_taken_down_before_it_is_due_is_skipped_and_nothing_is_sent()
    {
        await using var a = await Announcing.StartAsync();
        var discord = await a.AddOkAsync(new { network = "discord", url = DiscordUrl });
        var post = await a.OwnerPublishesAsync();
        await a.PassAsync();
        Assert.Equal(HttpStatusCode.OK, (await a.W.OwnerPostAsync($"/api/posts/{post}/unpublish")).Status);

        await a.PassAsync(minutes: 11);
        var row = await a.AnnouncementAsync(post, discord);
        Assert.Equal("skipped", row["state"]!.GetValue<string>());
        Assert.Equal("The post isn't live any more.", row["error"]!.GetValue<string>());
        Assert.Empty(a.Networks.At("https://discord.com/"));
    }

    [Fact]
    public async Task A_removed_account_is_cancelled_and_forgotten_and_a_new_one_hears_only_newer_posts()
    {
        await using var a = await Announcing.StartAsync();
        var discord = await a.AddOkAsync(new { network = "discord", url = DiscordUrl });
        var post = await a.OwnerPublishesAsync();
        // After the post went live, before the worker read it: still too late for this post.
        var slack = await a.AddOkAsync(new { network = "slack", url = SlackUrl });
        await a.PassAsync();
        Assert.Equal(HttpStatusCode.NoContent,
            (await a.W.Owner.SendAsync(HttpMethod.Delete, $"/api/blogs/{a.Blog}/social-accounts/{discord}", csrf: true)).Status);
        Assert.Equal(0L, await a.SqlAsync<long>("SELECT count(*) FROM social_secret WHERE account_id = @id", new { id = Guid.Parse(discord) }));
        Assert.Equal("cancelled", await a.SqlAsync<string>("SELECT state FROM announcement WHERE account_id = @id", new { id = Guid.Parse(discord) }));

        await a.PassAsync(minutes: 30);
        Assert.Empty(a.Networks.At("https://discord.com/"));
        Assert.Equal("none", (await a.AnnouncementAsync(post, slack))["state"]!.GetValue<string>());
        Assert.Empty(a.Networks.At("https://hooks.slack.com/"));

        // The owner can still send an older post by hand.
        Assert.Equal(HttpStatusCode.OK, (await a.ActAsync(post, slack, "send")).Status);
        await a.PassAsync();
        Assert.Single(a.Networks.At("https://hooks.slack.com/"));
    }

    [Fact]
    public async Task An_account_removed_while_its_announcement_is_on_the_way_is_judged_at_the_send()
    {
        await using var a = await Announcing.StartAsync();
        var discord = await a.AddOkAsync(new { network = "discord", url = DiscordUrl });
        var post = await a.OwnerPublishesAsync();
        await a.PassAsync();
        // The race the removal's own cancel can't close: the row is already scheduled when the account goes.
        await a.SqlAsync<int>("UPDATE social_account SET removed_at = now() WHERE id = @id RETURNING 1", new { id = Guid.Parse(discord) });
        await a.PassAsync(minutes: 11);
        Assert.Equal("cancelled", await a.SqlAsync<string>("SELECT state FROM announcement WHERE account_id = @id", new { id = Guid.Parse(discord) }));
        Assert.Empty(a.Networks.At("https://discord.com/"));
    }

    [Fact]
    public async Task A_lost_answer_is_unconfirmed_until_the_owner_looks_and_never_resent_on_its_own()
    {
        await using var a = await Announcing.StartAsync();
        var slack = await a.AddOkAsync(new { network = "slack", url = SlackUrl });
        var post = await a.OwnerPublishesAsync();
        a.Networks.Fail = r => r.Url.StartsWith("https://hooks.slack.com/", StringComparison.Ordinal) ? new HttpRequestException("reset") : null;
        await a.PassAsync(minutes: 11);
        a.Networks.Fail = null;
        Assert.Single(a.Networks.At("https://hooks.slack.com/"));
        // The connection broke after sending: it may be up, so it is tried again on the schedule…
        Assert.Equal("scheduled", (await a.AnnouncementAsync(post, slack))["state"]!.GetValue<string>());
        await a.PassAsync(minutes: 20);
        // …and that resumed try can't ask a webhook, so it stops as unconfirmed without sending.
        var row = await a.AnnouncementAsync(post, slack);
        Assert.Equal("unconfirmed", row["state"]!.GetValue<string>());
        Assert.Single(a.Networks.At("https://hooks.slack.com/"));
        Assert.Equal(0, await a.PassAsync(minutes: 60));
        Assert.Contains(await a.W.EventsAsync(), e => e.Kind == "announcement_unconfirmed");

        Assert.Equal(HttpStatusCode.Conflict, (await a.ActAsync(post, slack, "send")).Status);
        Assert.Equal(HttpStatusCode.OK, (await a.ActAsync(post, slack, "resend")).Status);
        await a.PassAsync();
        Assert.Equal(2, a.Networks.At("https://hooks.slack.com/").Count);
        Assert.Equal("posted", (await a.AnnouncementAsync(post, slack))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task It_s_there_marks_an_unconfirmed_one_posted_without_sending()
    {
        await using var a = await Announcing.StartAsync();
        var slack = await a.AddOkAsync(new { network = "slack", url = SlackUrl });
        var post = await a.OwnerPublishesAsync();
        a.Networks.Fail = _ => new HttpRequestException("reset");
        await a.PassAsync(minutes: 11);
        a.Networks.Fail = null;
        await a.PassAsync(minutes: 20);
        Assert.Equal(HttpStatusCode.OK, (await a.ActAsync(post, slack, "done")).Status);
        Assert.Equal("posted", (await a.AnnouncementAsync(post, slack))["state"]!.GetValue<string>());
        Assert.Equal(0, await a.PassAsync(minutes: 60));
        Assert.Single(a.Networks.At("https://hooks.slack.com/"));
    }

    [Fact]
    public async Task A_rate_limit_waits_and_refused_credentials_fail_on_the_account()
    {
        await using var a = await Announcing.StartAsync();
        var discord = await a.AddOkAsync(new { network = "discord", url = DiscordUrl });
        var post = await a.OwnerPublishesAsync();
        a.Networks.Answer = _ => SocialStub.Json("""{"message":"You are being rate limited.","retry_after":3600,"global":false}""", HttpStatusCode.TooManyRequests);
        await a.PassAsync(minutes: 11);
        var row = await a.AnnouncementAsync(post, discord);
        Assert.Equal("scheduled", row["state"]!.GetValue<string>());
        // Discord's hour, not the schedule's first minute.
        Assert.True(DateTime.Parse(row["due_at"]!.GetValue<string>()).ToUniversalTime() > DateTime.UtcNow.AddMinutes(50));
        Assert.Single(a.Networks.At("https://discord.com/"));

        a.Networks.Answer = _ => SocialStub.Json("""{"message":"Unknown Webhook","code":10015}""", HttpStatusCode.NotFound);
        await a.PassAsync(minutes: 120);
        row = await a.AnnouncementAsync(post, discord);
        Assert.Equal("failed", row["state"]!.GetValue<string>());
        Assert.Contains("Unknown Webhook", row["error"]!.GetValue<string>());
        Assert.Contains("Unknown Webhook", row["account"]!["last_error"]!.GetValue<string>());
        Assert.Contains(await a.W.EventsAsync(), e => e.Kind == "announcement_failed");
    }

    [Fact]
    public async Task The_default_text_shortens_to_fit_and_the_owners_own_text_is_refused_when_it_does_not()
    {
        await using var a = await Announcing.StartAsync();
        var bluesky = await a.AddOkAsync(new { network = "bluesky", handle = "blog.bsky.social", password = "abcd-efgh-ijkl-mnop" });
        var draft = await a.W.DraftAsync("A title", "Body.");
        var post = draft["id"]!.GetValue<string>();
        var excerpt = new string('x', 290);
        Assert.Equal("done", (await a.W.OkAsync("edit_post", new { post, base_revision = draft["revision"]!.GetValue<string>(), excerpt }))["status"]!.GetValue<string>());

        var composed = (await a.AnnouncementAsync(post, bluesky))["composed"]!;
        Assert.True(composed["fits"]!.GetValue<bool>());
        Assert.DoesNotContain("xxxx", composed["text"]!.GetValue<string>()); // the excerpt went
        Assert.StartsWith("A title", composed["text"]!.GetValue<string>());

        var tooLong = await a.W.Owner.SendAsync(HttpMethod.Put, $"/api/posts/{post}/announcements/{bluesky}", csrf: true, body: new { text = new string('y', 400) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.Status);
        Assert.Contains("300", tooLong.Json!["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Credentials_are_sealed_never_returned_and_an_account_password_is_refused()
    {
        await using var a = await Announcing.StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await a.AddAsync(new { network = "bluesky", handle = "me.bsky.social", password = "my-real-password" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.AddAsync(new { network = "discord", url = "https://evil.example/api/webhooks/1/t" })).Status);
        await a.AddOkAsync(new { network = "discord", url = DiscordUrl });
        await a.AddOkAsync(new { network = "telegram", token = "123456:AAH-telegram-secret_part", chat = "@blogchan" });

        var screen = (await a.W.Owner.GetJsonAsync($"/api/blogs/{a.Blog}/announce")).Json!.ToJsonString();
        Assert.DoesNotContain("announce-secret-token", screen);
        Assert.DoesNotContain("telegram-secret", screen);
        Assert.Contains("@blogchan", screen);
        await using var conn = new NpgsqlConnection(a.W.Host.App.OwnerConnectionString);
        foreach (var stored in await conn.QueryAsync<byte[]>("SELECT ciphertext FROM social_secret"))
        {
            Assert.DoesNotContain("announce-secret-token", Encoding.UTF8.GetString(stored));
            Assert.DoesNotContain("telegram-secret", Encoding.UTF8.GetString(stored));
        }
    }

    [Fact]
    public async Task Without_a_master_key_the_screen_says_what_to_set_and_the_blog_runs()
    {
        await using var a = await Announcing.StartAsync(secrets: false);
        var screen = (await a.W.Owner.GetJsonAsync($"/api/blogs/{a.Blog}/announce")).Json!;
        Assert.False(screen["available"]!.GetValue<bool>());
        Assert.Contains("Blog__Secrets__MasterKey", screen["reason"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await a.AddAsync(new { network = "slack", url = SlackUrl })).Status);
        await a.OwnerPublishesAsync();
        Assert.Equal(0, await a.PassAsync(minutes: 30));
    }

    [Fact]
    public async Task A_blog_with_no_public_address_fails_with_that_reason()
    {
        await using var a = await Announcing.StartAsync();
        var discord = await a.AddOkAsync(new { network = "discord", url = DiscordUrl });
        var post = await a.OwnerPublishesAsync();
        await a.PassAsync();
        // The server forgets its address (a self-hoster who never set it): read live, as the worker reads it.
        var config = a.W.Host.App.Factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        config["Blog:PublicUrl"] = "";
        await a.PassAsync(minutes: 11);
        config["Blog:PublicUrl"] = SignInHost.PublicUrl;
        var row = await a.AnnouncementAsync(post, discord);
        Assert.Equal("failed", row["state"]!.GetValue<string>());
        Assert.Contains("Blog__PublicUrl", row["error"]!.GetValue<string>());
        Assert.Empty(a.Networks.At("https://discord.com/"));
    }
}
