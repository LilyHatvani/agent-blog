using System.Net;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Themes (docs/specs/themes.md) through the real host: every built-in theme renders every page, readers try themes only
/// when the owner allows it, a reader's pick follows them and never reaches anyone else, the owner previews regardless
/// and sets the blog's theme.
/// </summary>
public sealed class ThemeTests
{
    private static readonly string[] Themes = ["paper", "ledger", "mono", "studio", "midnight"];

    private static async Task<(PostTests.World World, string BlogId)> StartAsync(bool picker)
    {
        var w = await PostTests.World.StartAsync();
        var blogId = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
        if (picker) Assert.Equal(HttpStatusCode.OK, (await SetAsync(w, blogId, new { theme_picker = true })).Status);
        await w.SetLevelAsync("publish");
        var post = (await w.OkAsync("create_draft", new
        {
            title = "A post", excerpt = "Short.", categories = new[] { "Guides" }, tags = new[] { "mcp" },
            body_md = "Words.\n\n## A heading\n\n| | A | B |\n| --- | --- | --- |\n| row | yes | no |\n\n> quoted\n\n```\ncode\n```",
        }))["post"]!;
        await w.OkAsync("publish_post", new { post = post["id"]!.GetValue<string>() });
        await w.OkAsync("publish_post", new { post = (await w.DraftAsync("Second one", "More."))["id"]!.GetValue<string>() });
        return (w, blogId);
    }

    private static Task<JsonResult> SetAsync(PostTests.World w, string blogId, object patch) =>
        w.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{blogId}", csrf: true, body: patch);

    private static async Task<(HttpResponseMessage Response, string Html)> GetAsync(HttpClient client, string path)
    {
        var r = await client.GetAsync(path);
        return (r, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Every_theme_renders_every_page_in_itself_not_the_fallback()
    {
        var (w, _) = await StartAsync(picker: true);
        await using var _w = w;
        var reader = w.Host.Browser().Http;
        foreach (var theme in Themes)
        {
            foreach (var (path, status) in new[] { ("/", 200), ("/posts/a-post", 200), ("/tag/mcp", 200), ("/category/guides", 200), ("/themes", 200), ("/no-such-page", 404) })
            {
                var (r, html) = await GetAsync(reader, $"{path}?theme={theme}");
                Assert.True((int)r.StatusCode == status, $"{theme} {path}: {r.StatusCode}");
                Assert.Contains($"class=\"theme-{theme}\"", html);
                Assert.Contains($"href=\"/assets/themes/{theme}.css\"", html);
                Assert.Contains("class=\"ab-picker\"", html);
            }
            var post = (await GetAsync(reader, $"/posts/a-post?theme={theme}")).Html;
            Assert.Contains("<table>", post);
            Assert.Contains("href=\"https://blog.test/category/guides\"", post);
            Assert.Contains("A post", (await GetAsync(reader, $"/?theme={theme}")).Html);
            Assert.Contains("Second one", (await GetAsync(reader, $"/?theme={theme}")).Html);
            var css = await reader.GetAsync($"/assets/themes/{theme}.css");
            Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/assets/themes/nope.css")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/assets/theme.css")).StatusCode); // pages cached before themes
    }

    [Fact]
    public async Task Without_the_owners_switch_readers_cannot_try_themes_and_nothing_is_set()
    {
        var (w, _) = await StartAsync(picker: false);
        await using var _w = w;
        var reader = w.Host.Browser().Http;

        var (r, html) = await GetAsync(reader, "/?theme=mono");
        Assert.Contains("class=\"theme-paper\"", html);
        Assert.DoesNotContain("ab-picker", html);
        Assert.False(r.Headers.Contains("Set-Cookie"));
        Assert.Equal("public, max-age=60", r.Headers.CacheControl!.ToString());

        // Even a forged cookie does nothing.
        var forged = new HttpRequestMessage(HttpMethod.Get, "/");
        forged.Headers.Add("Cookie", "blog_theme=mono");
        Assert.Contains("class=\"theme-paper\"", await (await w.Host.Browser().Http.SendAsync(forged)).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/themes")).StatusCode);
    }

    [Fact]
    public async Task A_readers_pick_follows_them_from_page_to_page_is_never_indexed_or_shared_and_back_goes_back()
    {
        var (w, _) = await StartAsync(picker: true);
        await using var _w = w;
        var reader = w.Host.Browser();

        var (first, html) = await GetAsync(reader.Http, "/?theme=mono");
        Assert.Contains("class=\"theme-mono\"", html);
        Assert.Contains("<meta name=\"robots\" content=\"noindex\">", html);
        Assert.Contains("Previewing <strong>Mono</strong>", html);
        Assert.Equal("no-store, private", string.Join(", ", first.Headers.CacheControl!.ToString().Split(", ").Order()));
        Assert.Contains("Cookie", first.Headers.Vary);

        // The next page, no parameter: still Mono for this browser; another browser sees the blog's own.
        Assert.Contains("class=\"theme-mono\"", (await GetAsync(reader.Http, "/posts/a-post")).Html);
        Assert.Contains("class=\"theme-paper\"", (await GetAsync(w.Host.Browser().Http, "/posts/a-post")).Html);

        // The bar's links keep the reader on the page they're on.
        var post = (await GetAsync(reader.Http, "/posts/a-post")).Html;
        Assert.Contains("href=\"/posts/a-post?theme=ledger\"", post);
        Assert.Contains("href=\"/posts/a-post?theme=own\"", post);

        // Back to the blog's own: the pick is forgotten.
        Assert.Contains("class=\"theme-paper\"", (await GetAsync(reader.Http, "/posts/a-post?theme=own")).Html);
        var after = (await GetAsync(reader.Http, "/")).Html;
        Assert.Contains("class=\"theme-paper\"", after);
        Assert.DoesNotContain("noindex", after);

        // A name that isn't a theme is the blog's own.
        Assert.Contains("class=\"theme-paper\"", (await GetAsync(reader.Http, "/?theme=../../etc")).Html);
    }

    [Fact]
    public async Task The_owner_sets_the_blogs_theme_and_previews_others_even_with_the_readers_bar_off()
    {
        var (w, blogId) = await StartAsync(picker: false);
        await using var _w = w;

        var set = await SetAsync(w, blogId, new { theme = "ledger" });
        Assert.Equal(HttpStatusCode.OK, set.Status);
        Assert.Equal("ledger", set.Json!["theme"]!.GetValue<string>());
        Assert.Equal(Themes.Length, set.Json["themes"]!.AsArray().Count);
        Assert.Contains(("blog_settings_changed", $"account:{(await w.Owner.GetJsonAsync("/api/me")).Json!["account"]!["id"]}"), await w.EventsAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(w, blogId, new { theme = "nope" })).Status);

        var reader = w.Host.Browser().Http;
        Assert.Contains("class=\"theme-ledger\"", (await GetAsync(reader, "/")).Html);
        Assert.Contains("class=\"theme-ledger\"", (await GetAsync(reader, "/?theme=studio")).Html); // readers still can't

        // The owner, signed in, previews and is told only they see the bar; their pages are never cached for others.
        var (r, html) = await GetAsync(w.Owner.Http, "/?theme=studio");
        Assert.Contains("class=\"theme-studio\"", html);
        Assert.Contains("only you see this bar", html);
        Assert.Contains("private", r.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.Http.GetAsync("/themes")).StatusCode);

        // With the switch on, the gallery marks the blog's own theme and links each one.
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(w, blogId, new { theme_picker = true })).Status);
        var gallery = (await GetAsync(w.Host.Browser().Http, "/themes")).Html;
        Assert.Contains("Ledger <span class=\"ab-badge\">this blog's theme</span>", gallery);
        foreach (var t in new[] { "mono", "studio", "paper" }) Assert.Contains($"href=\"https://blog.test/?theme={t}\"", gallery);
        Assert.Contains("href=\"https://blog.test/?theme=own\"", gallery);
    }
}
