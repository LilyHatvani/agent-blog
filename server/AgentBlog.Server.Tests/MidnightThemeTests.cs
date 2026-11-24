using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Midnight, Echo of Dreams' look (docs/specs/themes.md): every page renders in it, its fonts come from this site, and
/// under each post a creator card names each person who wrote it, their agents, their author page and their own link
/// (docs/specs/authors.md § The author's link), which only they set.
/// </summary>
public sealed class MidnightThemeTests
{
    private static string Rev(JsonNode post) => post["revision"]!.GetValue<string>();

    private static async Task<HttpResponseMessage> GetAsync(PeoplesAgentsTests.Crew c, string path) => await c.Host.Browser().Http.GetAsync(path);

    private static async Task<string> PageAsync(PeoplesAgentsTests.Crew c, string path)
    {
        var r = await GetAsync(c, path);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{path}: {r.StatusCode}");
        return await r.Content.ReadAsStringAsync();
    }

    /// <summary>Anna's agent drafts it, Anna adds to it, the owner publishes: one post, two writers behind one person.</summary>
    private static async Task<JsonNode> AnnaAndHerAgentPublishAsync(PeoplesAgentsTests.Crew c, string title)
    {
        var agent = await c.ConnectAsync(c.Anna, "Anna's laptop");
        var post = await c.DraftAsync(agent, title);
        post = (await c.Anna.SendAsync(HttpMethod.Patch, $"/api/posts/{post["id"]}", csrf: true,
            body: new { base_revision = Rev(post), body_md = "> A pull quote.\n\nIt rained, and the trams kept going." })).Json!["post"]!.AsObject();
        return (await c.Owner.PostJsonAsync($"/api/posts/{post["id"]}/publish", new { base_revision = Rev(post) }, csrf: true)).Json!["post"]!;
    }

    [Fact]
    public async Task Every_page_renders_in_Midnight_with_its_own_fonts_and_nothing_from_elsewhere()
    {
        await using var c = await PeoplesAgentsTests.Crew.StartAsync();
        var settings = (await c.Owner.GetJsonAsync($"/api/blogs/{c.Blog}")).Json!;
        Assert.Contains(settings["themes"]!.AsArray(), t => t!["name"]!.GetValue<string>() == "midnight");
        Assert.Equal(HttpStatusCode.OK, (await c.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{c.Blog}", csrf: true, body: new { theme = "midnight" })).Status);
        var post = await AnnaAndHerAgentPublishAsync(c, "Rain in Lisbon");

        foreach (var path in new[] { "/", $"/posts/{post["slug"]}", "/author/anna" })
        {
            var html = await PageAsync(c, path);
            Assert.Contains("/assets/themes/midnight.css", html);
            Assert.DoesNotContain("fonts.googleapis", html);
        }
        var notFound = await GetAsync(c, "/nothing-here");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Contains("/assets/themes/midnight.css", await notFound.Content.ReadAsStringAsync());

        // The fonts: from this site, as woff2, and the page lets them load (font-src 'self').
        var css = await (await GetAsync(c, "/assets/themes/midnight.css")).Content.ReadAsStringAsync();
        Assert.Contains("url('/assets/themes/midnight/fonts/quicksand-latin.woff2')", css);
        foreach (var file in System.Text.RegularExpressions.Regex.Matches(css, @"/assets/themes/midnight/fonts/([a-z0-9-]+\.woff2)").Select(m => m.Value).Distinct())
        {
            var font = await GetAsync(c, file);
            Assert.True(font.StatusCode == HttpStatusCode.OK, file);
            Assert.Equal("font/woff2", font.Content.Headers.ContentType!.MediaType);
            Assert.Equal("wOF2"u8.ToArray(), (await font.Content.ReadAsByteArrayAsync())[..4]);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(c, "/assets/themes/midnight/fonts/nope.woff2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(c, "/assets/themes/paper/fonts/quicksand-latin.woff2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(c, "/assets/themes/midnight/fonts/OFL-raleway.txt")).StatusCode);
        var page = await GetAsync(c, "/");
        Assert.Contains("font-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task The_creator_card_names_the_person_their_agents_their_page_and_their_own_link()
    {
        await using var c = await PeoplesAgentsTests.Crew.StartAsync();
        await c.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{c.Blog}", csrf: true, body: new { theme = "midnight" });
        var post = await AnnaAndHerAgentPublishAsync(c, "Porto by night");

        var before = await PageAsync(c, $"/posts/{post["slug"]}");
        var card = System.Text.RegularExpressions.Regex.Match(before, "<section class=\"creators\"[\\s\\S]*?</section>").Value;
        Assert.Contains("<a class=\"creator-name\" rel=\"author\" href=\"https://blog.test/author/anna\">Anna</a>", card);
        Assert.DoesNotContain("claude.ai", card); // the agent's name is not on the public page (docs/specs/bylines.md)
        Assert.DoesNotContain("Their profile", card);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(card, "class=\"creator\""));  // one person, not one per writer

        Assert.Equal(HttpStatusCode.NoContent, (await c.Anna.SendAsync(HttpMethod.Patch, $"/api/blogs/{c.Blog}/me/author", csrf: true,
            body: new { profile_url = "https://chat.echoofdreams.eu/@anna" })).Status);
        var after = await PageAsync(c, $"/posts/{post["slug"]}");
        Assert.Contains("<a rel=\"me noopener\" href=\"https://chat.echoofdreams.eu/@anna\">", after);
        Assert.Contains("https://chat.echoofdreams.eu/@anna", await PageAsync(c, "/author/anna"));

        // In Paper the author page carries it too.
        await c.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{c.Blog}", csrf: true, body: new { theme = "paper" });
        Assert.Contains("href=\"https://chat.echoofdreams.eu/@anna\">Their profile elsewhere</a>", await PageAsync(c, "/author/anna"));
    }

    [Fact]
    public async Task Only_a_safe_https_link_is_kept_and_each_person_sets_only_their_own()
    {
        await using var c = await PeoplesAgentsTests.Crew.StartAsync();
        Task<JsonResult> Set(SignInBrowser who, string url) =>
            who.SendAsync(HttpMethod.Patch, $"/api/blogs/{c.Blog}/me/author", csrf: true, body: new { profile_url = url });

        foreach (var bad in new[] { "http://example.com", "javascript:alert(1)", "https://user:pw@example.com/", "https://localhost/x",
                     "https://exa mple.com", "example.com/anna", "https://" + new string('a', 300) + ".com" })
            Assert.True((await Set(c.Anna, bad)).Status == HttpStatusCode.BadRequest, bad);

        Assert.Equal(HttpStatusCode.NoContent, (await Set(c.Anna, "  https://anna.example/about  ")).Status);
        Assert.Equal("https://anna.example/about", (await c.Anna.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["author_profile_url"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await Set(c.Anna, "")).Status);
        Assert.Null((await c.Anna.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["author_profile_url"]);

        // Ben's link is Ben's: setting one changes only the caller's own row.
        await Set(c.Ben, "https://ben.example/");
        await using var conn = new NpgsqlConnection(c.Host.App.OwnerConnectionString);
        Assert.Equal(1, await conn.QuerySingleAsync<int>("SELECT count(*)::int FROM author WHERE profile_url IS NOT NULL"));
        // A blog they aren't on: nothing to set.
        Assert.Equal(HttpStatusCode.NotFound, (await c.Ben.SendAsync(HttpMethod.Patch, $"/api/blogs/{Guid.NewGuid()}/me/author", csrf: true,
            body: new { profile_url = "https://x.example/" })).Status);

        // The table holds the line too.
        await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync("UPDATE author SET profile_url = 'javascript:alert(1)'"));
    }
}
