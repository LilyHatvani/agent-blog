using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentBlog.Announce;
using AgentBlog.Mail;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>Resend as the blog reaches it: every email recorded with its headers; answers scripted (200 with an id when none).</summary>
internal sealed class StubResend : HttpMessageHandler
{
    public sealed record Sent(string Url, Dictionary<string, string> Headers, JsonNode Json)
    {
        public string To => Json["to"]![0]!.GetValue<string>();
        public string Subject => Json["subject"]!.GetValue<string>();
        public string Text => Json["text"]!.GetValue<string>();
        public string Html => Json["html"]!.GetValue<string>();
    }

    private readonly ConcurrentQueue<Sent> _sent = new();

    public IReadOnlyList<Sent> All => [.. _sent];

    public Func<Sent, HttpResponseMessage?>? Answer { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var sent = new Sent(request.RequestUri!.AbsoluteUri, headers, JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
        _sent.Enqueue(sent);
        return Answer?.Invoke(sent) ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"re_123"}""", Encoding.UTF8, "application/json") };
    }
}

/// <summary>
/// M2 slice 9 (docs/specs/subscribe.md): readers subscribe under a post, confirm by pressing a button, get each new post
/// once through the owner's mail service, and leave with one click; addresses are sealed and never reach an agent.
/// </summary>
public sealed class SubscribeTests
{
    internal const string MasterKey = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string ApiKey = "re_secret_api_key_123456";

    /// <summary>The system clock moved on by <see cref="Ahead"/>: what a link's expiry is judged against.</summary>
    internal sealed class MovingClock : TimeProvider
    {
        public TimeSpan Ahead { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Ahead;
    }

    internal sealed class Mailing : IAsyncDisposable
    {
        public required MovingClock Clock { get; init; }

        public required PostTests.World W { get; init; }
        public required StubResend Resend { get; init; }
        public required string Blog { get; init; }

        public static async Task<Mailing> StartAsync(int perMinute = 1000, Action<IWebHostBuilder>? also = null)
        {
            var resend = new StubResend();
            var clock = new MovingClock();
            var w = await PostTests.World.StartAsync(b =>
            {
                b.UseSetting("Blog:Secrets:MasterKey", MasterKey);
                b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock));
                b.UseSetting("Blog:Subscribe:PerMinute", perMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
                b.ConfigureTestServices(s => s.AddHttpClient(MailQueue.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => resend));
                also?.Invoke(b);
            });
            var blog = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
            return new Mailing { W = w, Resend = resend, Blog = blog, Clock = clock };
        }

        public T Service<T>() where T : notnull => W.Host.App.Factory.Services.GetRequiredService<T>();

        public async Task<string> AddEmailAsync()
        {
            var r = await W.Owner.PostJsonAsync($"/api/blogs/{Blog}/social-accounts",
                new { network = "email", provider = "resend", api_key = ApiKey, from = "posts@blog.test", from_name = "The Blog" }, csrf: true);
            Assert.True(r.Status == HttpStatusCode.Created, r.Json?.ToJsonString());
            return r.Json!["account"]!["id"]!.GetValue<string>();
        }

        public HttpClient Reader() => W.Host.Browser().Http;

        public async Task<(HttpStatusCode Status, string Body)> PostFormAsync(string path, params (string, string)[] fields)
        {
            var r = await Reader().PostAsync(path, new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));
            return (r.StatusCode, await r.Content.ReadAsStringAsync());
        }

        public Task<(HttpStatusCode Status, string Body)> SubscribeAsync(string email, string? website = null) =>
            website is null ? PostFormAsync("/subscribe", ("email", email)) : PostFormAsync("/subscribe", ("email", email), ("website", website));

        public Task<int> MailPassAsync(int minutes = 0) => Service<MailQueue>().SendPassAsync(DateTimeOffset.UtcNow.AddMinutes(minutes), default);

        public async Task AnnouncePassAsync(int minutes)
        {
            await Service<Announcer>().CreatePassAsync(default);
            await Service<Announcer>().SendPassAsync(DateTimeOffset.UtcNow.AddMinutes(minutes), default);
        }

        /// <summary>A reader who subscribed and confirmed, through the pages and the confirmation email.</summary>
        public async Task ConfirmedAsync(string email)
        {
            Assert.Contains("Check your inbox", (await SubscribeAsync(email)).Body);
            await MailPassAsync();
            var link = ConfirmLink(Resend.All.Last(m => m.To == email));
            var page = await Reader().GetStringAsync(link);
            var s = Regex.Match(page, "name=\"s\" value=\"([^\"]+)\"").Groups[1].Value;
            var t = Regex.Match(page, "name=\"t\" value=\"([^\"]+)\"").Groups[1].Value;
            Assert.Contains("You're subscribed", (await PostFormAsync("/subscribe/confirm", ("s", s), ("t", t))).Body);
        }

        public static string ConfirmLink(StubResend.Sent mail) =>
            new Uri(Regex.Match(mail.Text, @"https://blog\.test/subscribe/confirm\?\S+").Value).PathAndQuery;

        public static string UnsubscribeLink(StubResend.Sent mail) =>
            new Uri(mail.Json["headers"]!["List-Unsubscribe"]!.GetValue<string>().Trim('<', '>')).PathAndQuery;

        public async Task<string> OwnerPublishesAsync(string title, string excerpt = "What it's about.")
        {
            var draft = await W.DraftAsync(title, "Body of the post.");
            var id = draft["id"]!.GetValue<string>();
            Assert.Equal("done", (await W.OkAsync("edit_post", new { post = id, base_revision = draft["revision"]!.GetValue<string>(), excerpt }))["status"]!.GetValue<string>());
            Assert.Equal(HttpStatusCode.OK, (await W.OwnerPostAsync($"/api/posts/{id}/publish")).Status);
            return id;
        }

        public async Task<T> SqlAsync<T>(string sql, object? args = null)
        {
            await using var conn = new NpgsqlConnection(W.Host.App.OwnerConnectionString);
            return await conn.QuerySingleAsync<T>(sql, args);
        }

        public ValueTask DisposeAsync() => W.DisposeAsync();
    }

    [Fact]
    public async Task Without_an_email_account_there_is_no_form_and_the_page_says_so()
    {
        await using var m = await Mailing.StartAsync();
        var r = await m.Reader().GetAsync("/subscribe");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Contains("doesn't send posts by email", await r.Content.ReadAsStringAsync());
        var post = (await m.W.DraftAsync("A post", "Words."))["id"]!.GetValue<string>();
        await m.W.OwnerPostAsync($"/api/posts/{post}/publish");
        Assert.DoesNotContain("ab-subscribe", await m.Reader().GetStringAsync("/posts/a-post"));
    }

    [Fact]
    public async Task A_reader_subscribes_under_a_post_confirms_with_a_button_and_gets_the_next_post_once()
    {
        await using var m = await Mailing.StartAsync();
        await m.AddEmailAsync();
        await m.OwnerPublishesAsync("First post");
        await m.AnnouncePassAsync(11); // nobody yet: the announcement goes to nobody
        var page = await m.Reader().GetStringAsync("/posts/first-post");
        Assert.Contains("action=\"https://blog.test/subscribe\"", page);
        Assert.Contains("form-action 'self'", (await m.Reader().GetAsync("/posts/first-post")).Headers.GetValues("Content-Security-Policy").Single());

        Assert.Contains("Check your inbox", (await m.SubscribeAsync("  Reader@Example.COM ")).Body);
        Assert.Equal(1, await m.MailPassAsync());
        var confirm = Assert.Single(m.Resend.All);
        Assert.Equal("reader@example.com", confirm.To);
        Assert.Equal("The Blog <posts@blog.test>", confirm.Json["from"]!.GetValue<string>());
        Assert.Equal("Bearer " + ApiKey, confirm.Headers["Authorization"]);
        var blogTitle = (await m.W.Owner.GetJsonAsync($"/api/blogs/{m.Blog}")).Json!["title"]!.GetValue<string>();
        Assert.Equal($"Confirm your subscription to {blogTitle}", confirm.Subject);

        // Opening the link (as a mail scanner does) confirms nobody: only the button does.
        var link = Mailing.ConfirmLink(confirm);
        Assert.Contains("name=\"t\"", await m.Reader().GetStringAsync(link));
        Assert.Equal("pending", await m.SqlAsync<string>("SELECT state FROM subscriber"));
        var s = Regex.Match(link, "s=([^&]+)").Groups[1].Value;
        var t = Regex.Match(link, "t=([^&]+)").Groups[1].Value;
        Assert.Contains("You're subscribed", (await m.PostFormAsync("/subscribe/confirm", ("s", s), ("t", t))).Body);
        Assert.Contains("run out", (await m.PostFormAsync("/subscribe/confirm", ("s", s), ("t", t))).Body); // used

        var second = await m.OwnerPublishesAsync("Second post", "The second one's excerpt.");
        await m.AnnouncePassAsync(11);
        Assert.Equal(1, await m.MailPassAsync());
        var mail = m.Resend.All.Last();
        Assert.Equal("Second post", mail.Subject);
        Assert.Contains("The second one's excerpt.", mail.Text);
        Assert.Contains("Read it: https://blog.test/posts/second-post", mail.Text); // the plain address: nothing added, no tracking
        Assert.Contains("href=\"https://blog.test/posts/second-post\"", mail.Html);
        Assert.DoesNotContain("utm_", mail.Html);
        Assert.Equal("List-Unsubscribe=One-Click", mail.Json["headers"]!["List-Unsubscribe-Post"]!.GetValue<string>());
        Assert.StartsWith("agentblog-mail-", mail.Headers["Idempotency-Key"]);
        Assert.Equal(0, await m.MailPassAsync(30)); // once
        Assert.Equal(2, m.Resend.All.Count);
    }

    [Fact]
    public async Task The_form_answers_the_same_whoever_is_on_the_list_and_floods_nobody()
    {
        await using var m = await Mailing.StartAsync();
        await m.AddEmailAsync();
        await m.ConfirmedAsync("on.list@example.com");
        var sent = m.Resend.All.Count;

        var known = await m.SubscribeAsync("on.list@example.com");
        var fresh = await m.SubscribeAsync("new@example.com");
        var again = await m.SubscribeAsync("new@example.com"); // waiting, within the hour
        Assert.Equal(HttpStatusCode.OK, known.Status);
        Assert.Equal(Strip(known.Body), Strip(fresh.Body));
        Assert.Equal(Strip(fresh.Body), Strip(again.Body));
        await m.MailPassAsync();
        Assert.Equal(sent + 1, m.Resend.All.Count); // one confirmation, to the new address only
        Assert.Equal("new@example.com", m.Resend.All.Last().To);

        // Even past the hour, a confirmed reader who fills the form again stays confirmed and gets nothing.
        m.Clock.Ahead = TimeSpan.FromHours(2);
        await m.SubscribeAsync("on.list@example.com");
        await m.MailPassAsync(120);
        Assert.Equal("confirmed", await m.SqlAsync<string>("SELECT state FROM subscriber WHERE confirmed_at IS NOT NULL"));
        Assert.DoesNotContain(m.Resend.All.Skip(sent + 1), x => x.To == "on.list@example.com");
        m.Clock.Ahead = TimeSpan.Zero;

        // A bot that fills the field people don't see is told it worked, and nothing happens.
        Assert.Contains("Check your inbox", (await m.SubscribeAsync("bot@example.com", website: "https://spam.example")).Body);
        Assert.Equal(2L, await m.SqlAsync<long>("SELECT count(*) FROM subscriber"));

        var bad = await m.SubscribeAsync("not an address");
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Contains("doesn't look right", bad.Body);
    }

    private static string Strip(string html) => Regex.Replace(html, "<input[^>]*>", "");

    [Fact]
    public async Task Too_many_tries_from_one_place_are_refused()
    {
        await using var m = await Mailing.StartAsync(perMinute: 2);
        await m.AddEmailAsync();
        await m.SubscribeAsync("a@example.com");
        await m.SubscribeAsync("b@example.com");
        var third = await m.SubscribeAsync("c@example.com");
        Assert.Equal(HttpStatusCode.TooManyRequests, third.Status);
        Assert.Contains("Too many tries", third.Body);
        Assert.Equal(2L, await m.SqlAsync<long>("SELECT count(*) FROM subscriber"));
    }

    [Fact]
    public async Task One_click_unsubscribe_erases_the_address_and_nothing_more_is_sent()
    {
        await using var m = await Mailing.StartAsync();
        await m.AddEmailAsync();
        await m.ConfirmedAsync("leaving@example.com");
        await m.OwnerPublishesAsync("One");
        await m.AnnouncePassAsync(11);
        await m.MailPassAsync();
        var mail = m.Resend.All.Last();

        // The mail client's own button: a POST with List-Unsubscribe=One-Click to the header's address (RFC 8058).
        var path = Mailing.UnsubscribeLink(mail);
        var r = await m.Reader().PostAsync(path, new FormUrlEncodedContent([new("List-Unsubscribe", "One-Click")]));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("You're unsubscribed", await r.Content.ReadAsStringAsync());
        Assert.Equal("unsubscribed", await m.SqlAsync<string>("SELECT state FROM subscriber"));
        Assert.True(await m.SqlAsync<bool>("SELECT email_ciphertext IS NULL FROM subscriber"));

        var count = m.Resend.All.Count;
        await m.OwnerPublishesAsync("Two");
        await m.AnnouncePassAsync(11);
        await m.MailPassAsync();
        Assert.Equal(count, m.Resend.All.Count);

        // A tampered link does nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await m.Reader().PostAsync(path[..^2] + "00", null)).StatusCode);
    }

    [Fact]
    public async Task A_post_taken_down_while_its_emails_wait_stops_the_rest()
    {
        await using var m = await Mailing.StartAsync();
        await m.AddEmailAsync();
        await m.ConfirmedAsync("one@example.com");
        await m.ConfirmedAsync("two@example.com");
        var post = await m.OwnerPublishesAsync("Gone soon");
        await m.AnnouncePassAsync(11); // queued for both
        Assert.Equal(2L, await m.SqlAsync<long>("SELECT count(*) FROM mail_out WHERE kind = 'post'"));
        Assert.Equal(HttpStatusCode.OK, (await m.W.OwnerPostAsync($"/api/posts/{post}/unpublish")).Status);
        var before = m.Resend.All.Count;
        await m.MailPassAsync();
        Assert.Equal(before, m.Resend.All.Count);
        Assert.Equal(2L, await m.SqlAsync<long>("SELECT count(*) FROM mail_out WHERE kind = 'post' AND status = 'skipped'"));
    }

    [Fact]
    public async Task A_reader_whose_address_waits_again_gets_no_post_and_a_post_is_queued_once()
    {
        await using var m = await Mailing.StartAsync();
        await m.AddEmailAsync();
        await m.ConfirmedAsync("reader@example.com");
        await m.OwnerPublishesAsync("Queued once");
        await m.AnnouncePassAsync(11);
        var announcement = await m.SqlAsync<Guid>("SELECT announcement_id FROM mail_out WHERE kind = 'post'");
        // A second enqueue (a resumed attempt) adds nobody twice.
        Assert.Equal(1, await MailQueue.EnqueuePostAsync(m.Service<AgentBlog.Database.Db>(), announcement, "Again", DateTimeOffset.UtcNow, default));
        // Not confirmed any more when it's sent (here set directly): skipped, not sent.
        await m.SqlAsync<int>("UPDATE subscriber SET state = 'pending' RETURNING 1");
        var before = m.Resend.All.Count;
        await m.MailPassAsync();
        Assert.Equal(before, m.Resend.All.Count);
        Assert.Equal("skipped", await m.SqlAsync<string>("SELECT status FROM mail_out WHERE kind = 'post'"));
    }

    [Fact]
    public async Task A_refused_key_holds_the_queue_and_says_so_and_a_rate_limit_waits()
    {
        await using var m = await Mailing.StartAsync();
        var account = await m.AddEmailAsync();
        await m.ConfirmedAsync("reader@example.com");
        await m.OwnerPublishesAsync("Held");
        await m.AnnouncePassAsync(11);

        m.Resend.Answer = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"statusCode":429,"message":"Too many requests","name":"rate_limit_exceeded"}"""),
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(20)) },
        };
        await m.MailPassAsync();
        Assert.True(await m.SqlAsync<bool>("SELECT next_at > now() + interval '15 minutes' FROM mail_out WHERE kind = 'post'"));

        m.Resend.Answer = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent($$"""{"statusCode":401,"message":"API key {{ApiKey}} is invalid","name":"invalid_api_key"}"""),
        };
        await m.MailPassAsync(30);
        var listed = (await m.W.Owner.GetJsonAsync($"/api/blogs/{m.Blog}/announce")).Json!["accounts"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == account)!;
        Assert.Contains("Resend refused the API key", listed["last_error"]!.GetValue<string>());
        Assert.DoesNotContain(ApiKey, listed["last_error"]!.GetValue<string>());
        Assert.Equal("pending", await m.SqlAsync<string>("SELECT status FROM mail_out WHERE kind = 'post'"));
        Assert.True(await m.SqlAsync<bool>("SELECT next_at > now() + interval '1 hour' FROM mail_out WHERE kind = 'post'"));
    }

    [Fact]
    public async Task Addresses_are_sealed_the_owner_sees_and_removes_them_and_no_agent_does()
    {
        await using var m = await Mailing.StartAsync();
        await m.AddEmailAsync();
        await m.ConfirmedAsync("private@example.com");

        await using (var conn = new NpgsqlConnection(m.W.Host.App.OwnerConnectionString))
        {
            var stored = await conn.QuerySingleAsync<byte[]>("SELECT email_ciphertext FROM subscriber");
            Assert.DoesNotContain("private@example.com", Encoding.UTF8.GetString(stored));
        }
        var list = (await m.W.Owner.GetJsonAsync($"/api/blogs/{m.Blog}/subscribers")).Json!;
        Assert.Equal(1, list["confirmed"]!.GetValue<int>());
        var one = list["subscribers"]![0]!;
        Assert.Equal("private@example.com", one["email"]!.GetValue<string>());

        // Nothing an agent can call says anything about subscribers.
        foreach (var tool in new[] { "get_blog", "list_posts" })
            Assert.DoesNotContain("private@example.com", (await m.W.OkAsync(tool)).ToJsonString());

        Assert.Equal(HttpStatusCode.NoContent,
            (await m.W.Owner.SendAsync(HttpMethod.Delete, $"/api/blogs/{m.Blog}/subscribers/{one["id"]}", csrf: true)).Status);
        Assert.True(await m.SqlAsync<bool>("SELECT state = 'unsubscribed' AND email_ciphertext IS NULL FROM subscriber"));
    }

    [Fact]
    public async Task A_post_an_agent_published_on_its_own_emails_nobody_until_the_owner_taps()
    {
        await using var m = await Mailing.StartAsync();
        var account = await m.AddEmailAsync();
        await m.ConfirmedAsync("reader@example.com");
        var before = m.Resend.All.Count;
        await m.W.SetLevelAsync("publish");
        var post = (await m.W.DraftAsync("Agent's own", "Words."))["id"]!.GetValue<string>();
        await m.W.OkAsync("publish_post", new { post });
        await m.AnnouncePassAsync(60);
        await m.MailPassAsync(60);
        Assert.Equal(before, m.Resend.All.Count);
        Assert.Equal(HttpStatusCode.OK, (await m.W.OwnerPostAsync($"/api/posts/{post}/announcements/{account}/send")).Status);
        await m.AnnouncePassAsync(0);
        await m.MailPassAsync(0);
        Assert.Equal(before + 1, m.Resend.All.Count);
    }

    [Fact]
    public async Task A_confirmation_link_that_ran_out_or_was_altered_confirms_nobody()
    {
        await using var m = await Mailing.StartAsync();
        await m.AddEmailAsync();
        await m.SubscribeAsync("late@example.com");
        await m.MailPassAsync();
        var link = Mailing.ConfirmLink(m.Resend.All.Single());
        async Task<string> Page(string path)
        {
            var r = await m.Reader().GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            return await r.Content.ReadAsStringAsync();
        }
        Assert.Contains("run out", await Page(link[..^2] + (link.EndsWith('0') ? "11" : "00")));
        Assert.Contains("name=\"t\"", await m.Reader().GetStringAsync(link)); // good now…
        m.Clock.Ahead = TimeSpan.FromHours(49);
        Assert.Contains("run out", await Page(link)); // …not after 48 hours
        Assert.Equal("pending", await m.SqlAsync<string>("SELECT state FROM subscriber"));
    }

    [Fact]
    public async Task The_owner_sends_a_test_and_sees_what_the_service_answered()
    {
        await using var m = await Mailing.StartAsync();
        var account = await m.AddEmailAsync();
        var ok = (await m.W.Owner.PostJsonAsync($"/api/blogs/{m.Blog}/social-accounts/{account}/test", new { to = "me@example.com" }, csrf: true)).Json!;
        Assert.True(ok["sent"]!.GetValue<bool>());
        Assert.Equal("me@example.com", m.Resend.All.Single().To);
        m.Resend.Answer = _ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("""{"statusCode":422,"message":"The posts@blog.test domain is not verified.","name":"validation_error"}"""),
        };
        var refused = (await m.W.Owner.PostJsonAsync($"/api/blogs/{m.Blog}/social-accounts/{account}/test", new { to = "me@example.com" }, csrf: true)).Json!;
        Assert.False(refused["sent"]!.GetValue<bool>());
        Assert.Contains("domain is not verified", refused["message"]!.GetValue<string>());
        // One Email account per blog: a second would send each reader the post twice.
        Assert.Equal(HttpStatusCode.BadRequest, (await m.W.Owner.PostJsonAsync($"/api/blogs/{m.Blog}/social-accounts",
            new { network = "email", provider = "resend", api_key = ApiKey, from = "other@blog.test" }, csrf: true)).Status);
    }
}
