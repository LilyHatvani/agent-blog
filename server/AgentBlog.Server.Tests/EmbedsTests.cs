using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// The comments slot (docs/specs/embeds.md): a sandboxed, cross-origin iframe of the widget's page for each post. With no
/// widget set, a post page and its CSP are exactly what they were; with one, the page carries the frame (its address the
/// owner's template with the post's uuid) and the CSP allows framing exactly the widget's origin plus the blog's own
/// resize script by its exact address. No widget script ever runs on the blog's origin. The resize script takes a height
/// only from that frame's origin, only as <c>{type:'resize', height:n}</c>, clamped.
/// </summary>
public sealed class EmbedsTests
{
    /// <summary>Today's public-page CSP, written out here so a change to the server's constant shows up as a failure.</summary>
    private const string TodaysCsp = "default-src 'none'; img-src https: 'self'; style-src 'self'; font-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private const string Template = "https://widgets.example/comments/{post}";

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
        Assert.DoesNotContain("<iframe", before.Body);
        Assert.DoesNotContain("<script src", before.Body); // JSON-LD is data, never run
        Assert.False((await b.W.Owner.GetJsonAsync($"/api/blogs/{b.Id}/embeds/comments")).Json!["set"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.OK, (await b.PutAsync(new { url_template = Template })).Status);
        Assert.Contains("<iframe", (await b.ReadAsync($"/posts/{b.Slug}")).Body);

        Assert.Equal(HttpStatusCode.OK, (await b.W.Owner.SendAsync(HttpMethod.Delete, $"/api/blogs/{b.Id}/embeds/comments", csrf: true)).Status);
        var after = await b.ReadAsync($"/posts/{b.Slug}");
        Assert.Equal(before.Csp, after.Csp);
        Assert.Equal(before.Body, after.Body);
    }

    [Fact]
    public async Task With_a_widget_the_post_page_frames_it_sandboxed_and_the_CSP_adds_only_its_frame_origin_and_the_resize_script()
    {
        await using var b = await Blog.StartAsync();
        var saved = await b.PutAsync(new { url_template = "https://widgets.example:8443/c/{post}/thread" });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.Json?.ToJsonString());
        Assert.Equal("https://widgets.example:8443", saved.Json!["frame_origin"]!.GetValue<string>());

        var page = await b.ReadAsync($"/posts/{b.Slug}");
        Assert.Contains($"<iframe data-embed=\"comments\" data-post=\"{b.PostId}\" data-origin=\"https://widgets.example:8443\" " +
            $"src=\"https://widgets.example:8443/c/{b.PostId}/thread\" sandbox=\"allow-scripts allow-same-origin allow-popups allow-forms\" " +
            "loading=\"lazy\" title=\"Comments\" width=\"100%\" height=\"600\"></iframe>", page.Body);
        Assert.Contains("<script src=\"/assets/embed-resize.js\" defer></script>", page.Body);
        Assert.Equal("default-src 'none'; img-src https: 'self'; style-src 'self'; font-src 'self'; " +
                     "script-src https://blog.test/assets/embed-resize.js; frame-src https://widgets.example:8443; " +
                     "base-uri 'none'; form-action 'self'; frame-ancestors 'none'", page.Csp);
        Assert.DoesNotContain("connect-src", page.Csp);
        Assert.DoesNotContain("widgets.example", page.Csp.Split("frame-src")[0]); // the widget's origin is never a script source

        // In Midnight too (the other theme with its own post page).
        await b.W.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{b.Id}", csrf: true, body: new { theme = "midnight" });
        Assert.Contains($"data-post=\"{b.PostId}\"", (await b.ReadAsync($"/posts/{b.Slug}")).Body);

        // Every other page keeps today's CSP and carries no frame or script.
        foreach (var path in new[] { "/", "/tag/talk", "/no-such-page" })
        {
            var other = await b.ReadAsync(path);
            Assert.Equal(TodaysCsp, other.Csp);
            Assert.DoesNotContain("<iframe", other.Body);
            Assert.DoesNotContain("<script src", other.Body);
        }
    }

    [Theory]
    [InlineData("http://widgets.example/comments/{post}")]               // not https
    [InlineData("javascript:alert(1)//{post}")]
    [InlineData("//widgets.example/comments/{post}")]
    [InlineData("https://user:pass@widgets.example/comments/{post}")]    // credentials
    [InlineData("https://widgets.example/comments?post={post}")]         // {post} in the query, and a query
    [InlineData("https://widgets.example/comments/{post}?theme=dark")]   // a query
    [InlineData("https://widgets.example/comments/{post}#top")]          // a fragment
    [InlineData("https://*.widgets.example/comments/{post}")]            // a wildcard
    [InlineData("https://widgets.example/comments")]                     // no {post}
    [InlineData("https://widgets.example/{post}/{post}")]                // {post} twice
    [InlineData("https://{post}.widgets.example/c")]                     // {post} outside the path
    [InlineData("https://widgets.example/c/{post}\"><script>")]          // quotes and markup
    [InlineData("https://widgets.example/c/{post}; script-src *")]
    public async Task A_template_that_is_not_an_https_path_with_one_post_placeholder_is_refused_at_save(string template)
    {
        await using var b = await Blog.StartAsync();
        var r = await b.PutAsync(new { url_template = template });
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Equal("invalid_argument", r.Json!["code"]!.GetValue<string>());
        Assert.False((await b.W.Owner.GetJsonAsync($"/api/blogs/{b.Id}/embeds/comments")).Json!["set"]!.GetValue<bool>());
        Assert.Equal(TodaysCsp, (await b.ReadAsync($"/posts/{b.Slug}")).Csp);
    }

    [Fact]
    public async Task The_resize_script_takes_a_clamped_numeric_height_only_from_the_frames_origin_and_nothing_else()
    {
        await using var b = await Blog.StartAsync();
        var served = await b.W.Host.Browser().Http.GetAsync("/assets/embed-resize.js");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.StartsWith("text/javascript", served.Content.Headers.ContentType!.ToString());
        var script = await served.Content.ReadAsStringAsync();

        const string Widget = "https://widgets.example";
        object[] cases =
        [
            new { data = (object?)new { type = "resize", height = 420 }, origin = Widget },                    // 420
            new { data = (object?)new { type = "resize", height = 420 }, origin = "https://evil.example" },    // another origin
            new { data = (object?)new { type = "resize", height = 420 }, origin = "https://widgets.example.evil.test" },
            new { data = (object?)new { type = "resize", height = 420 }, origin = "null" },                    // an opaque origin
            new { data = (object?)new { type = "resize", height = "420" }, origin = Widget },                  // a string height
            new { data = (object?)new { type = "resize", height = new { x = 1 } }, origin = Widget },          // an object height
            new { data = (object?)new { type = "resize" }, origin = Widget },                                  // no height
            new { data = (object?)new { type = "resize", height = 420, html = "<b>" }, origin = Widget },      // anything else carried
            new { data = (object?)new { type = "navigate", height = 420 }, origin = Widget },                  // another type
            new { data = (object?)"resize", origin = Widget },
            new { data = (object?)null, origin = Widget },
            new { data = (object?)new[] { 420 }, origin = Widget },
            new { data = (object?)new { type = "resize", height = 1_000_000 }, origin = Widget },              // clamped down
            new { data = (object?)new { type = "resize", height = -5 }, origin = Widget },                     // clamped up
            new { data = (object?)new { type = "resize", height = 300.6 }, origin = Widget },                  // whole pixels
        ];
        var results = RunNode(script, $"agentBlogEmbedHeight(c.data, c.origin, '{Widget}')", cases);
        Assert.Equal(["420", "null", "null", "null", "null", "null", "null", "null", "null", "null", "null", "null", "5000", "100", "301"], results);

        // No allowed origin known (the frame lost its attribute): nothing is accepted, not even from an opaque origin.
        Assert.Equal(["null"], RunNode(script, "agentBlogEmbedHeight(c.data, c.origin, null)",
            [new { data = (object?)new { type = "resize", height = 420 }, origin = "null" }]));
    }

    /// <summary>Runs the served script under Node with each case, returning what the expression gave for each, as JSON.</summary>
    private static List<string> RunNode(string script, string expression, object[] cases)
    {
        var dir = Directory.CreateTempSubdirectory("agentblog-embed-");
        try
        {
            var file = Path.Combine(dir.FullName, "run.js");
            File.WriteAllText(file, script + $$"""

                const cases = JSON.parse(require('fs').readFileSync(process.argv[2], 'utf8'));
                console.log(JSON.stringify(cases.map((c) => JSON.stringify({{expression}}))));
                """);
            var input = Path.Combine(dir.FullName, "cases.json");
            File.WriteAllText(input, JsonSerializer.Serialize(cases));
            using var node = Process.Start(new ProcessStartInfo("node", [file, input]) { RedirectStandardOutput = true, RedirectStandardError = true })
                ?? throw new InvalidOperationException("Node.js is needed to run the resize script's test.");
            var output = node.StandardOutput.ReadToEnd();
            var error = node.StandardError.ReadToEnd();
            node.WaitForExit();
            Assert.True(node.ExitCode == 0, error);
            return [.. JsonNode.Parse(output)!.AsArray().Select(x => x!.GetValue<string>())];
        }
        finally
        {
            dir.Delete(recursive: true);
        }
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

        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsync(new { url_template = Template }, editor)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await editor.GetJsonAsync($"/api/blogs/{b.Id}/embeds/comments")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.PutAsync(new { url_template = Template }, csrf: false)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.PutAsync(new { url_template = Template }, b.W.Host.Browser())).Status);
        Assert.Equal(TodaysCsp, (await b.ReadAsync($"/posts/{b.Slug}")).Csp);
    }
}
