using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Midnight's home masthead: the blog's most-used tags sit in the rows' date column, most used first, only tags with a
/// live post, at most <see cref="AgentBlog.Public.PublicSite.TopTags"/>, and only on the first page.
/// </summary>
public sealed class MidnightMastheadTests
{
    private static async Task<string> ReaderAsync(PostTests.World w, string path)
    {
        var r = await w.Host.Browser().Http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadAsStringAsync();
    }

    private static async Task LiveAsync(PostTests.World w, string title, string[] tags, bool publish = true)
    {
        var post = (await w.OkAsync("create_draft", new { title, body_md = "Words.", tags }))["post"]!;
        if (publish) await w.OkAsync("publish_post", new { post = post["id"]!.GetValue<string>() });
    }

    private static string TagsNav(string html) => Regex.Match(html, "<nav class=\"masthead-tags\"[\\s\\S]*?</nav>").Value;

    [Fact]
    public async Task The_home_masthead_lists_the_most_used_live_tags_most_used_first_on_the_first_page_only()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{blog}", csrf: true, body: new { theme = "midnight" })).Status);
        await w.SetLevelAsync("publish");

        // A blog with no tags shows no tag column.
        await LiveAsync(w, "Untagged", []);
        Assert.Equal("", TagsNav(await ReaderAsync(w, "/")));

        // Trauma on three live posts, Memory on two, nine more on one each; Draftonly only on a draft.
        await LiveAsync(w, "One", ["Trauma", "Memory", "A1"]);
        await LiveAsync(w, "Two", ["Trauma", "Memory", "A2"]);
        await LiveAsync(w, "Three", ["Trauma", "A3", "A4", "A5", "A6", "A7", "A8", "A9"]);
        await LiveAsync(w, "Draft", ["Draftonly"], publish: false);

        var nav = TagsNav(await ReaderAsync(w, "/"));
        var names = Regex.Matches(nav, "<a href=\"[^\"]+\">([^<]+)</a>").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(AgentBlog.Public.PublicSite.TopTags, names.Count);
        Assert.Equal(new[] { "Trauma", "Memory" }, names.Take(2));
        Assert.DoesNotContain("Draftonly", nav);
        Assert.Contains("href=\"https://blog.test/tag/trauma\">Trauma</a>", nav);

        // Past the first page there is no masthead, so no tags either.
        for (var i = 0; i < AgentBlog.Public.PublicSite.PageSize; i++) await LiveAsync(w, $"Filler {i}", []);
        Assert.Equal("", TagsNav(await ReaderAsync(w, "/?page=2")));
    }
}
