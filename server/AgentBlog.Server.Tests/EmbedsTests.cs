using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// The comments slot (docs/specs/embeds.md): with no widget set, a post page and its CSP are exactly what they were;
/// with one set, the page carries the post's uuid and the widget's script, and the CSP allows exactly the widget's
/// origin for script and exactly the listed origins for connections, on post pages only. Only the owner sets it, and
/// only https origins pass.
/// </summary>
public sealed class EmbedsTests
{
    /// <summary>Today's public-page CSP, written out here so a change to the server's constant shows up as a failure.</summary>
    private const string TodaysCsp = "default-src 'none'; img-src https: 'self'; style-src 'self'; font-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private const string Script = "https://widgets.example/v1/comments.js";

    private sealed class Blog : IAsyncDisposable
    {
        public required PostTests.World W { get; init; }
        public required string Id { get; init; }
        public required string PostId { get; init; }
        public required string Slug { get; init; }

        public static async Task<Blog> StartAsync()
        {
            var w = await PostTests.World.StartAsync();
            var id = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
            var post = (await w.OkAsync("create_draft", new { title = "With comments", body_md = "Say something.", tags = new[] { "talk" } }))["post"]!;
            var published = await w.OwnerPostAsync($"/api/posts/{post["id"]}/publish");
            Assert.True(published.Status == HttpStatusCode.OK, published.Json?.ToJsonString());
            return new Blog { W = w, Id = id, PostId = post["id"]!.GetValue<string>(), Slug = post["slug"]!.GetValue<string>() };
        }

        public Task<JsonResult> PutAsync(object body, SignInBrowser? as_ = null, bool csrf = true) =>
            (as_ ?? W.Owner).SendAsync(HttpMethod.Put, $"/api/blogs/{Id}/embeds/comments", csrf, body);

        public async Task<(string Body, string Csp, HttpStatusCode Status)> ReadAsync(string path)
        {
            var r = await W.Host.Browser().Http.GetAsync(path); // a reader: no cookies
            return (await r.Content.ReadAsStringAsync(), r.Headers.GetValues("Content-Security-Policy").Single(), r.StatusCode);
        }

        public ValueTask DisposeAsync() => W.DisposeAsync();
    }

    [Fact]
    public async Task Without_a_widget_a_post_page_and_its_CSP_are_what_they_were_and_clearing_one_puts_them_back_byte_for_byte()
    {
        await using var b = await Blog.StartAsync();
        var before = await b.ReadAsync($"/posts/{b.Slug}");
        Assert.Equal(HttpStatusCode.OK, before.Status);
        Assert.Equal(TodaysCsp, before.Csp);
        Assert.DoesNotContain("data-embed", before.Body);
        Assert.DoesNotContain("<script src", before.Body); // JSON-LD is data, never run
        Assert.False((await b.W.Owner.GetJsonAsync($"/api/blogs/{b.Id}/embeds/comments")).Json!["set"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.OK, (await b.PutAsync(new { script_url = Script, connect_origins = new[] { "https://api.widgets.example" } })).Status);
        Assert.Contains("data-embed", (await b.ReadAsync($"/posts/{b.Slug}")).Body);

        Assert.Equal(HttpStatusCode.OK, (await b.W.Owner.SendAsync(HttpMethod.Delete, $"/api/blogs/{b.Id}/embeds/comments", csrf: true)).Status);
        var after = await b.ReadAsync($"/posts/{b.Slug}");
        Assert.Equal(before.Csp, after.Csp);
        Assert.Equal(before.Body, after.Body);
    }

    [Fact]
    public async Task With_a_widget_the_post_page_carries_its_uuid_and_script_and_the_CSP_allows_exactly_those_origins_on_post_pages_only()
    {
        await using var b = await Blog.StartAsync();
        var saved = await b.PutAsync(new
        {
            script_url = Script,
            // A trailing slash and a repeat are tidied, not refused.
            connect_origins = new[] { "https://api.widgets.example/", "https://live.widgets.example:8443", "https://api.widgets.example" },
        });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.Json?.ToJsonString());
        Assert.Equal("https://widgets.example", saved.Json!["script_origin"]!.GetValue<string>());

        var page = await b.ReadAsync($"/posts/{b.Slug}");
        Assert.Contains($"<div data-embed=\"comments\" data-post=\"{b.PostId}\"></div>", page.Body);
        Assert.Contains($"<script src=\"{Script}\" defer></script>", page.Body);
        Assert.Equal("default-src 'none'; img-src https: 'self'; style-src 'self'; font-src 'self'; script-src https://widgets.example; " +
                     "connect-src https://api.widgets.example https://live.widgets.example:8443; base-uri 'none'; form-action 'self'; frame-ancestors 'none'",
            page.Csp);

        // In Midnight too (the other theme with its own post page).
        await b.W.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{b.Id}", csrf: true, body: new { theme = "midnight" });
        Assert.Contains($"data-post=\"{b.PostId}\"", (await b.ReadAsync($"/posts/{b.Slug}")).Body);

        // Every other page keeps today's CSP and carries no script.
        foreach (var path in new[] { "/", "/tag/talk", "/no-such-page" })
        {
            var other = await b.ReadAsync(path);
            Assert.Equal(TodaysCsp, other.Csp);
            Assert.DoesNotContain("<script src", other.Body);
        }

        // No connections listed: no connect-src at all (default-src 'none' still covers it).
        Assert.Equal(HttpStatusCode.OK, (await b.PutAsync(new { script_url = Script, connect_origins = Array.Empty<string>() })).Status);
        Assert.Equal("default-src 'none'; img-src https: 'self'; style-src 'self'; font-src 'self'; script-src https://widgets.example; " +
                     "base-uri 'none'; form-action 'self'; frame-ancestors 'none'", (await b.ReadAsync($"/posts/{b.Slug}")).Csp);
    }

    [Theory]
    [InlineData("http://widgets.example/c.js", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("https://user:pass@widgets.example/c.js", null)]
    [InlineData("//widgets.example/c.js", null)]
    [InlineData(Script, "http://api.widgets.example")]
    [InlineData(Script, "https://api.widgets.example; script-src *")]
    [InlineData(Script, "https://*.widgets.example")]
    [InlineData(Script, "https://api.widgets.example/path")]
    [InlineData(Script, "https://api.widgets.example?x=1")]
    [InlineData(Script, "https://u:p@api.widgets.example")]
    [InlineData(Script, "'unsafe-inline'")]
    [InlineData(Script, "*")]
    public async Task Anything_but_https_origins_is_refused_at_save_and_nothing_reaches_the_page(string script, string? connect)
    {
        await using var b = await Blog.StartAsync();
        var r = await b.PutAsync(new { script_url = script, connect_origins = connect is null ? Array.Empty<string>() : new[] { connect } });
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Equal("invalid_argument", r.Json!["code"]!.GetValue<string>());
        Assert.False((await b.W.Owner.GetJsonAsync($"/api/blogs/{b.Id}/embeds/comments")).Json!["set"]!.GetValue<bool>());
        Assert.Equal(TodaysCsp, (await b.ReadAsync($"/posts/{b.Slug}")).Csp);
    }

    [Fact]
    public async Task Only_the_owner_sets_the_widget_an_editor_finds_nothing_and_a_write_needs_the_CSRF_header()
    {
        await using var b = await Blog.StartAsync();
        var invited = await b.W.Owner.PostJsonAsync($"/api/blogs/{b.Id}/invitations", new { role = "editor" }, csrf: true);
        var link = invited.Json!["link"]!.GetValue<string>();
        var editor = b.W.Host.Browser();
        var begin = await editor.PostJsonAsync("/api/auth/invite", new { token = link[(link.LastIndexOf('/') + 1)..], provider = "discord" });
        Assert.Equal("/admin?joined=1", (await editor.CompleteAtProviderAsync(new Uri(begin.Json!["url"]!.GetValue<string>()),
            new StubAccount("400000000000000001", "Ed", null))).Location);

        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsync(new { script_url = Script }, editor)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await editor.GetJsonAsync($"/api/blogs/{b.Id}/embeds/comments")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.PutAsync(new { script_url = Script }, csrf: false)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.PutAsync(new { script_url = Script }, b.W.Host.Browser())).Status);
        Assert.Equal(TodaysCsp, (await b.ReadAsync($"/posts/{b.Slug}")).Csp);
    }
}
