using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentBlog.Public;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SkiaSharp;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>IndexNow and a WebSub hub as the blog sees them: every request recorded; answers scripted per host (200 when none).</summary>
internal sealed class StubSearch : HttpMessageHandler
{
    public sealed record Hit(string Url, string ContentType, string Body);

    public ConcurrentQueue<Hit> Hits { get; } = new();
    private readonly ConcurrentDictionary<string, HttpStatusCode> _answers = new();

    public void Answer(string host, HttpStatusCode status) => _answers[host] = status;

    public IEnumerable<Hit> To(string host) => Hits.Where(h => new Uri(h.Url).Host == host);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Hits.Enqueue(new Hit(request.RequestUri!.AbsoluteUri, request.Content?.Headers.ContentType?.MediaType ?? "", body));
        return new HttpResponseMessage(_answers.GetValueOrDefault(request.RequestUri.Host, HttpStatusCode.OK));
    }
}

/// <summary>
/// M2 slice 6 (docs/specs/social.md): a shared link shows a picture and the post's title; search engines read
/// structured data; IndexNow and WebSub hear when posts change, never about what isn't public.
/// </summary>
public sealed class SocialTests
{
    private const string IndexNow = "https://indexnow.test/indexnow";
    private const string Hub = "https://hub.test/";

    private sealed class Social : IAsyncDisposable
    {
        public required MediaTests.MediaWorld M { get; init; }
        public required StubSearch Search { get; init; }

        public static async Task<Social> StartAsync(string? hub = Hub)
        {
            var search = new StubSearch();
            var m = await MediaTests.MediaWorld.StartAsync(more: b =>
            {
                b.UseSetting("Blog:IndexNow:Endpoint", IndexNow);
                b.UseSetting("Blog:WebSub:Hub", hub ?? "");
                b.ConfigureTestServices(s => s.AddHttpClient(SearchPing.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => search));
            });
            return new Social { M = m, Search = search };
        }

        public PostTests.World W => M.World;

        public async Task<string> PageAsync(string path)
        {
            var r = await M.ReaderGetAsync(path);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return await r.Content.ReadAsStringAsync();
        }

        public async Task<JsonNode> LiveAsync(string title, string body, string? excerpt = null)
        {
            var post = (await M.OkAsync("create_draft", new { title, body_md = body, excerpt }))["post"]!;
            Assert.Equal(HttpStatusCode.OK, (await W.OwnerPostAsync($"/api/posts/{post["id"]}/publish")).Status);
            return await M.OkAsync("read_post", new { post = post["id"]!.GetValue<string>() });
        }

        public Task<int> PingAsync() => W.Host.App.Factory.Services.GetRequiredService<SearchPing>().RunOnceAsync(default);

        public ValueTask DisposeAsync() => M.DisposeAsync();
    }

    private static string? Meta(string html, string property) =>
        Regex.Match(html, $"<meta (?:property|name)=\"{Regex.Escape(property)}\" content=\"([^\"]*)\">") is { Success: true } m
            ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    private static JsonNode JsonLd(string html) =>
        JsonNode.Parse(Regex.Match(html, "<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value)!;

    private static string Path(string absolute) => new Uri(absolute).PathAndQuery;

    [Fact]
    public async Task A_post_with_its_own_picture_shares_that_picture_and_one_without_shares_its_card()
    {
        await using var s = await Social.StartAsync();
        var picture = await s.M.UploadAsync(MediaHost.Png);
        var withPicture = await s.LiveAsync("With a picture", $"Words.\n\n![A harbour at dawn]({picture["path"]})");
        var html = await s.PageAsync($"/posts/{withPicture["slug"]}");
        Assert.Equal($"https://blog.test{picture["path"]}", Meta(html, "og:image"));
        Assert.Equal("A harbour at dawn", Meta(html, "og:image:alt"));
        Assert.Equal("summary_large_image", Meta(html, "twitter:card"));
        Assert.Null(Meta(html, "og:image:width"));

        var plain = await s.LiveAsync("Without one", "Just words.");
        html = await s.PageAsync($"/posts/{plain["slug"]}");
        var card = Meta(html, "og:image")!;
        Assert.StartsWith($"https://blog.test/cards/posts/{plain["slug"]}.png?v=", card);
        Assert.Equal("1200", Meta(html, "og:image:width"));
        Assert.Equal(card, Meta(html, "twitter:image"));
        Assert.Equal($"https://blog.test/posts/{plain["slug"]}", Regex.Match(html, "<link rel=\"canonical\" href=\"([^\"]+)\">").Groups[1].Value);

        // A picture moved to the trash isn't shared: the card takes its place.
        await s.W.OwnerPostAsync($"/api/media/{picture["id"]}/trash");
        Assert.StartsWith("https://blog.test/cards/", Meta(await s.PageAsync($"/posts/{withPicture["slug"]}"), "og:image"));

        // …unless the post has another of its own: the first that is ready is shared.
        var second = await s.M.UploadAsync(MediaHost.Png);
        var two = await s.LiveAsync("Two pictures", $"![Gone]({picture["path"]})\n\n![A boat]({second["path"]})");
        var shared = await s.PageAsync($"/posts/{two["slug"]}");
        Assert.Equal($"https://blog.test{second["path"]}", Meta(shared, "og:image"));
        Assert.Equal("A boat", Meta(shared, "og:image:alt"));
    }

    [Fact]
    public async Task A_card_is_a_1200_by_630_png_for_live_posts_only_and_changes_address_with_its_title()
    {
        await using var s = await Social.StartAsync();
        var post = await s.LiveAsync("A title worth sharing", "Words.");
        var card = Meta(await s.PageAsync($"/posts/{post["slug"]}"), "og:image")!;
        var r = await s.M.ReaderGetAsync(Path(card));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/png", r.Content.Headers.ContentType!.MediaType);
        using (var bitmap = SKBitmap.Decode(await r.Content.ReadAsByteArrayAsync()))
            Assert.Equal((1200, 630), (bitmap.Width, bitmap.Height));
        Assert.Equal(HttpStatusCode.NotModified, (await s.M.ReaderGetAsync(Path(card), r.Headers.ETag!.Tag)).StatusCode);

        // Another title is another address, so platforms that cached the old picture fetch the new one.
        await s.W.Owner.SendAsync(HttpMethod.Patch, $"/api/posts/{post["id"]}", csrf: true, body: new { base_revision = post["revision"]!.GetValue<string>(), title = "A better title" });
        Assert.NotEqual(card, Meta(await s.PageAsync($"/posts/{post["slug"]}"), "og:image"));

        // Nothing readers can't see: a draft, or no post, has no card.
        var draft = (await s.M.OkAsync("create_draft", new { title = "Secret draft", body_md = "x" }))["post"]!;
        Assert.Equal(HttpStatusCode.NotFound, (await s.M.ReaderGetAsync($"/cards/posts/{draft["slug"]}.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.M.ReaderGetAsync("/cards/posts/nope.png")).StatusCode);
    }

    [Fact]
    public async Task Each_theme_draws_its_own_card_and_any_alphabet_fits()
    {
        await using var s = await Social.StartAsync();
        var blog = s.M.BlogId;
        var drawn = new List<string>();
        foreach (var theme in new[] { "paper", "ledger", "mono", "studio" })
        {
            await s.W.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{blog}", csrf: true, body: new { theme });
            var bytes = await (await s.M.ReaderGetAsync("/cards/blog.png")).Content.ReadAsByteArrayAsync();
            using var bitmap = SKBitmap.Decode(bytes);
            Assert.Equal(1200, bitmap.Width);
            drawn.Add(Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(bytes)));
        }
        Assert.Equal(4, drawn.Distinct().Count());

        foreach (var title in new[] { "Žluťoučký kůň úpěl ďábelské ódy", "Παράδειγμα τίτλου", "Пример заголовка", new string('W', 300) })
        {
            var post = await s.LiveAsync(title, "Words.");
            var r = await s.M.ReaderGetAsync(Path(Meta(await s.PageAsync($"/posts/{post["slug"]}"), "og:image")!));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
    }

    [Fact]
    public async Task Search_engines_read_structured_data_that_no_title_can_break_out_of()
    {
        await using var s = await Social.StartAsync();
        var post = await s.LiveAsync("Breaking </script><script>alert(1)</script> out", "Words.", excerpt: "What it's about.");
        var html = await s.PageAsync($"/posts/{post["slug"]}");
        Assert.DoesNotContain("<script>alert(1)", html);
        var ld = JsonLd(html);
        Assert.Equal("BlogPosting", ld["@type"]!.GetValue<string>());
        Assert.Equal("Breaking </script><script>alert(1)</script> out", ld["headline"]!.GetValue<string>());
        Assert.Equal("What it's about.", ld["description"]!.GetValue<string>());
        Assert.Equal(Meta(html, "og:image"), ld["image"]![0]!.GetValue<string>());
        Assert.NotNull(ld["datePublished"]);
        Assert.NotNull(Meta(html, "article:modified_time"));

        var home = await s.PageAsync("/");
        Assert.Equal("Blog", JsonLd(home)["@type"]!.GetValue<string>());
        Assert.StartsWith("https://blog.test/cards/blog.png?v=", Meta(home, "og:image"));
    }

    [Fact]
    public async Task Feeds_name_their_hub_when_the_server_has_one()
    {
        await using (var s = await Social.StartAsync())
        {
            Assert.Contains($"<atom:link href=\"{Hub}\" rel=\"hub\" />", await s.PageAsync("/feed.xml"));
            Assert.Contains($"<link href=\"{Hub}\" rel=\"hub\" />", await s.PageAsync("/atom.xml"));
        }
        await using (var off = await Social.StartAsync(hub: null))
        {
            Assert.DoesNotContain("rel=\"hub\"", await off.PageAsync("/feed.xml"));
            Assert.DoesNotContain("rel=\"hub\"", await off.PageAsync("/atom.xml"));
        }
    }

    [Fact]
    public async Task A_published_post_is_pinged_with_a_key_the_site_publishes_and_the_hub_hears_about_both_feeds()
    {
        await using var s = await Social.StartAsync();
        var post = await s.LiveAsync("Fresh", "Words.");
        Assert.Equal(1, await s.PingAsync());

        var ping = JsonNode.Parse(Assert.Single(s.Search.To("indexnow.test")).Body)!;
        Assert.Equal("blog.test", ping["host"]!.GetValue<string>());
        var key = ping["key"]!.GetValue<string>();
        Assert.Matches("^[0-9a-f]{32}$", key);
        Assert.Equal($"https://blog.test/{key}.txt", ping["keyLocation"]!.GetValue<string>());
        Assert.Equal([$"https://blog.test/posts/{post["slug"]}"], ping["urlList"]!.AsArray().Select(u => u!.GetValue<string>()));
        Assert.Equal(key, await s.PageAsync($"/{key}.txt"));
        Assert.Equal(HttpStatusCode.NotFound, (await s.M.ReaderGetAsync($"/{new string('0', 32)}.txt")).StatusCode);

        var hub = s.Search.To("hub.test").ToList();
        Assert.Equal(2, hub.Count);
        Assert.All(hub, h => Assert.Contains("hub.mode=publish", h.Body));
        Assert.Contains(hub, h => h.Body.Contains(Uri.EscapeDataString("https://blog.test/feed.xml")));
        Assert.Contains(hub, h => h.Body.Contains(Uri.EscapeDataString("https://blog.test/atom.xml")));

        var state = (await s.W.Owner.GetJsonAsync($"/api/blogs/{s.M.BlogId}")).Json!;
        Assert.True(state["search_ping"]!.GetValue<bool>());
        Assert.Equal(200, state["pings"]!.AsArray().Single(p => p!["service"]!.GetValue<string>() == "indexnow")!["status"]!.GetValue<int>());

        Assert.Equal(0, await s.PingAsync()); // once
    }

    [Fact]
    public async Task What_was_never_public_is_never_named_and_what_leaves_is()
    {
        await using var s = await Social.StartAsync();
        var draft = (await s.M.OkAsync("create_draft", new { title = "Not yet", body_md = "x" }))["post"]!;
        await s.M.OkAsync("edit_post", new { post = draft["id"]!.GetValue<string>(), base_revision = draft["revision"]!.GetValue<string>(), body_md = "y" });
        await s.W.OwnerPostAsync($"/api/posts/{draft["id"]}/trash");
        Assert.Equal(0, await s.PingAsync());
        Assert.Empty(s.Search.Hits);

        // A live post taken down is named: the engines should learn it's gone.
        var live = await s.LiveAsync("Going away", "Words.");
        await s.PingAsync();
        await s.W.OwnerPostAsync($"/api/posts/{live["id"]}/unpublish");
        Assert.Equal(1, await s.PingAsync());
        Assert.Contains($"posts/{live["slug"]}", s.Search.To("indexnow.test").Last().Body);
    }

    [Fact]
    public async Task The_owner_can_turn_it_off_and_sees_what_the_last_ping_got()
    {
        await using var s = await Social.StartAsync();
        s.Search.Answer("indexnow.test", HttpStatusCode.UnprocessableEntity);
        await s.LiveAsync("First", "Words.");
        await s.PingAsync();
        var failed = (await s.W.Owner.GetJsonAsync($"/api/blogs/{s.M.BlogId}")).Json!["pings"]!.AsArray()
            .Single(p => p!["service"]!.GetValue<string>() == "indexnow")!;
        Assert.Equal(422, failed["status"]!.GetValue<int>());
        Assert.Contains("422", failed["error"]!.GetValue<string>());

        var off = await s.W.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{s.M.BlogId}", csrf: true, body: new { search_ping = false });
        Assert.False(off.Json!["search_ping"]!.GetValue<bool>());
        var before = s.Search.Hits.Count;
        await s.LiveAsync("Second", "Words.");
        Assert.Equal(0, await s.PingAsync());
        Assert.Equal(before, s.Search.Hits.Count);
    }
}
