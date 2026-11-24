using System.Net;
using Dapper;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// M2b slice 6 (docs/specs/old-addresses.md): a blog that moved here keeps its old addresses. A live post's old
/// /{slug}, a recorded old path, Echo of Dreams' /api/blog/ feed and sitemap, and /login answer 301 to where the page is
/// now; nothing else changes, and a redirect never leaves the blog.
/// </summary>
public sealed class OldAddressTests
{
    private static async Task<(HttpStatusCode Status, string? Location)> GetAsync(PostTests.World w, string path)
    {
        var r = await w.Host.Browser().Http.GetAsync(path);
        return (r.StatusCode, r.Headers.Location?.OriginalString);
    }

    private static async Task<string> LiveAsync(PostTests.World w, string title)
    {
        var post = (await w.Owner.PostJsonAsync($"/api/blogs/{await BlogAsync(w)}/posts", new { title, body_md = "Words." }, csrf: true)).Json!["post"]!;
        var live = await w.Owner.PostJsonAsync($"/api/posts/{post["id"]}/publish", new { base_revision = post["revision"]!.GetValue<string>() }, csrf: true);
        return live.Json!["post"]!["slug"]!.GetValue<string>();
    }

    private static async Task<string> BlogAsync(PostTests.World w) =>
        (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();

    [Fact]
    public async Task A_live_posts_old_slug_address_moves_permanently_and_nothing_else_does()
    {
        await using var w = await PostTests.World.StartAsync();
        var slug = await LiveAsync(w, "Rain in Lisbon");
        var draft = (await w.OkAsync("create_draft", new { title = "Not yet", body_md = "x" }))["post"]!["slug"]!.GetValue<string>();

        Assert.Equal((HttpStatusCode.MovedPermanently, $"/posts/{slug}"), await GetAsync(w, $"/{slug}"));
        Assert.Equal((HttpStatusCode.MovedPermanently, $"/posts/{slug}?utm_source=x"), await GetAsync(w, $"/{slug}/?utm_source=x"));
        // A draft's slug, an unknown one and a deeper path are still not found; the real pages answer as before.
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(w, $"/{draft}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(w, "/nothing-here")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(w, $"/x/{slug}")).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(w, $"/posts/{slug}")).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(w, "/")).Status);
    }

    [Fact]
    public async Task Echo_of_Dreams_feed_and_sitemap_addresses_and_login_move_to_their_places()
    {
        await using var w = await PostTests.World.StartAsync();
        Assert.Equal((HttpStatusCode.MovedPermanently, "/feed.xml"), await GetAsync(w, "/api/blog/feed.xml"));
        Assert.Equal((HttpStatusCode.MovedPermanently, "/sitemap.xml"), await GetAsync(w, "/api/blog/sitemap.xml"));
        // EoD had an Atom feed too; its address moves with the query it came with.
        Assert.Equal((HttpStatusCode.MovedPermanently, "/atom.xml"), await GetAsync(w, "/api/blog/atom.xml"));
        Assert.Equal((HttpStatusCode.MovedPermanently, "/atom.xml?utm_source=x"), await GetAsync(w, "/api/blog/atom.xml?utm_source=x"));
        Assert.Equal((HttpStatusCode.MovedPermanently, "/sign-in"), await GetAsync(w, "/login"));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(w, "/api/blog/other.xml")).Status);
    }

    [Fact]
    public async Task A_recorded_old_path_moves_to_its_page_and_a_row_can_never_point_off_the_blog()
    {
        await using var w = await PostTests.World.StartAsync();
        var slug = await LiveAsync(w, "Porto");
        var blog = await BlogAsync(w);
        await using var conn = new NpgsqlConnection(w.Host.App.AppConnectionString);
        await conn.ExecuteAsync("INSERT INTO old_address (blog_id, path, target) VALUES (@blog::uuid, '/author/anna-from-eod', '/author/anna'), (@blog::uuid, '/2024/porto', @target)",
            new { blog, target = $"/posts/{slug}" });

        Assert.Equal((HttpStatusCode.MovedPermanently, $"/posts/{slug}"), await GetAsync(w, "/2024/porto"));
        Assert.Equal((HttpStatusCode.MovedPermanently, $"/posts/{slug}"), await GetAsync(w, "/2024/porto/"));
        Assert.Equal((HttpStatusCode.MovedPermanently, "/author/anna"), await GetAsync(w, "/author/anna-from-eod"));

        foreach (var target in new[] { "https://elsewhere.example/x", "//elsewhere.example/x", "posts/x", "/ spaced" })
            await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
                "INSERT INTO old_address (blog_id, path, target) VALUES (@blog::uuid, '/bad', @target)", new { blog, target }));
    }
}
