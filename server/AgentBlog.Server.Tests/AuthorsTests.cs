using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// M2b slice 3 (docs/specs/authors.md): each person who writes has an author page, /author/{slug}, in every theme; every
/// byline links each name to its person's page; another person's agent goes by its person's name too, so two people's
/// "Claude" are never one name.
/// </summary>
public sealed class AuthorsTests
{
    private static string Rev(JsonNode post) => post["revision"]!.GetValue<string>();

    /// <summary>Anna drafts and submits; the owner publishes it. Returns the live post.</summary>
    private static async Task<JsonNode> AnnaPublishesAsync(PeoplesAgentsTests.Crew c, string title)
    {
        var post = await c.PersonDraftAsync(c.Anna, title);
        post = (await c.Anna.PostJsonAsync($"/api/posts/{post["id"]}/submit", new { base_revision = Rev(post) }, csrf: true)).Json!["post"]!.AsObject();
        return (await c.Owner.PostJsonAsync($"/api/posts/{post["id"]}/publish", new { base_revision = Rev(post) }, csrf: true)).Json!["post"]!;
    }

    private static async Task<string> PageAsync(PeoplesAgentsTests.Crew c, string path)
    {
        var r = await c.Host.Browser().Http.GetAsync(path);
        return r.StatusCode == HttpStatusCode.OK ? await r.Content.ReadAsStringAsync() : $"[{(int)r.StatusCode}]";
    }

    private static async Task<T> SqlAsync<T>(PeoplesAgentsTests.Crew c, string sql, object? args = null)
    {
        await using var conn = new NpgsqlConnection(c.Host.App.OwnerConnectionString);
        return await conn.QuerySingleAsync<T>(sql, args);
    }

    [Fact]
    public async Task Everyone_gets_a_slug_from_their_name_once_and_keeps_it()
    {
        await using var c = await PeoplesAgentsTests.Crew.StartAsync();
        var me = async (SignInBrowser who) => (await who.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["author_path"]!.GetValue<string>();
        Assert.Equal("/author/lenka", await me(c.Owner));
        Assert.Equal("/author/anna", await me(c.Anna));

        // Accents go, as in post slugs; a second "Anna" gets her own address.
        var novak = await c.JoinAsync("writer", new StubAccount("500000000000000001", "Anna Nováková", null));
        var twin = await c.JoinAsync("writer", new StubAccount("500000000000000002", "Anna", null));
        Assert.Equal("/author/anna-novakova", await me(novak));
        Assert.Equal("/author/anna-2", await me(twin));

        // Removed and invited again: the same address, so old links still work.
        var annaId = await c.AccountIdAsync("Anna Nováková");
        await c.Owner.SendAsync(HttpMethod.Delete, $"/api/blogs/{c.Blog}/people/{annaId}", csrf: true);
        Assert.Equal("anna-novakova", await SqlAsync<string>(c, "SELECT slug FROM author WHERE account_id = @id::uuid", new { id = annaId }));
        var back = await c.JoinAsync("writer", new StubAccount("500000000000000001", "Anna Nováková", null));
        Assert.Equal("/author/anna-novakova", await me(back));

        // Names in other scripts, and letters that don't decompose.
        Assert.Equal("lodz-strasse", await SqlAsync<string>(c, "SELECT author_slug_base('Łódź Straße')"));
        Assert.Equal("author", await SqlAsync<string>(c, "SELECT author_slug_base('山田')"));
    }

    [Fact]
    public async Task A_byline_links_each_name_to_its_author_page_and_names_another_persons_agent_with_its_person()
    {
        await using var c = await PeoplesAgentsTests.Crew.StartAsync();
        var annas = await AnnaPublishesAsync(c, "Lisbon in rain");
        var html = await PageAsync(c, $"/posts/{annas["slug"]}");
        // Anna wrote, the owner let it out: her name links to her page, the owner's to theirs.
        Assert.Contains("href=\"https://blog.test/author/anna\">Anna</a>", html);
        Assert.Contains("approved by", html);
        Assert.Contains("href=\"https://blog.test/author/lenka\">Lenka</a>", html);
        Assert.Contains("\"url\":\"https://blog.test/author/anna\"", html.Replace(" ", "")); // JSON-LD Person url

        // Anna's agent: readers see Anna, through MCP (the agent's name is not on the page); approved by the owner.
        var agent = await c.ConnectAsync(c.Anna, "Anna's laptop");
        var post = await c.DraftAsync(agent, "Porto by night");
        var request = (await c.OkAsync(agent, "publish_post", new { post = post["id"]!.ToString() }))["request"]!["id"]!.GetValue<string>();
        await c.Owner.PostJsonAsync($"/api/requests/{request}/approve", null, csrf: true);
        var agentHtml = await PageAsync(c, $"/posts/{post["slug"]}");
        var agentByline = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Match(agentHtml, "<p class=\"byline\">(.*?)</p>").Groups[1].Value, "<[^>]+>", ""));
        Assert.Equal("Written by Anna · through MCP · approved by Lenka", agentByline);
        Assert.DoesNotContain("claude.ai", agentByline); // the agent's name is in structured data only, not the byline

        // The owner's own agent: readers see the owner, through MCP.
        var owners = await c.DraftAsync(c.W.Agent, "From the owner's agent");
        var asked = (await c.OkAsync(c.W.Agent, "publish_post", new { post = owners["id"]!.ToString() }))["request"]!["id"]!.GetValue<string>();
        await c.Owner.PostJsonAsync($"/api/requests/{asked}/approve", null, csrf: true);
        var ownersHtml = await PageAsync(c, $"/posts/{owners["slug"]}");
        var ownersByline = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Match(ownersHtml, "<p class=\"byline\">(.*?)</p>").Groups[1].Value, "<[^>]+>", ""));
        Assert.Equal("By Lenka · through MCP", ownersByline);
        Assert.Contains("href=\"https://blog.test/author/lenka\">Lenka</a>", ownersHtml);
    }

    [Fact]
    public async Task An_author_page_lists_the_live_posts_a_person_and_their_agents_wrote_and_nothing_else()
    {
        await using var c = await PeoplesAgentsTests.Crew.StartAsync();
        var hers = await AnnaPublishesAsync(c, "Hers, live");
        await c.PersonDraftAsync(c.Anna, "Hers, a draft");
        var agent = await c.ConnectAsync(c.Anna, "Claude");
        var byAgent = await c.DraftAsync(agent, "Her agent's, live");
        var request = (await c.OkAsync(agent, "publish_post", new { post = byAgent["id"]!.ToString() }))["request"]!["id"]!.GetValue<string>();
        await c.Ben.PostJsonAsync($"/api/requests/{request}/approve", null, csrf: true);
        var owners = await c.PersonDraftAsync(c.Owner, "The owner's own");
        await c.Owner.PostJsonAsync($"/api/posts/{owners["id"]}/publish", new { base_revision = Rev(owners) }, csrf: true);

        var page = await PageAsync(c, "/author/anna");
        Assert.Contains("<h1>Anna</h1>", page);
        Assert.Contains(": 2 posts.", page); // the agent's name is not on the public page (docs/specs/bylines.md)
        Assert.DoesNotContain("claude.ai", page);
        Assert.Contains("Hers, live", page);
        Assert.Contains("Her agent&#39;s, live", page.Replace("&#x27;", "&#39;"));
        Assert.DoesNotContain("Hers, a draft", page);
        Assert.DoesNotContain("The owner&#39;s own", page.Replace("&#x27;", "&#39;"));
        Assert.Contains("<meta property=\"og:type\" content=\"profile\">", page);

        // Ben approved one and only put an old revision back on another: approving and restoring write nothing, so he
        // has no page; nor does an unknown slug.
        var revisions = (await c.Owner.GetJsonAsync($"/api/posts/{hers["id"]}")).Json!["revisions"]!.AsArray();
        await c.Anna.SendAsync(HttpMethod.Patch, $"/api/posts/{hers["id"]}", csrf: true, body: new { base_revision = Rev(hers), title = "Hers, live!" });
        await c.Ben.PostJsonAsync($"/api/posts/{hers["id"]}/revisions/{revisions[0]!["id"]}/restore", null, csrf: true);
        Assert.Equal("[404]", await PageAsync(c, "/author/ben"));
        Assert.Equal("[404]", await PageAsync(c, "/author/nobody"));
        Assert.Contains("Hers, live", await PageAsync(c, "/author/anna"));

        // Readers see no names while bylines are off: no author pages either.
        await c.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{c.Blog}", csrf: true, body: new { bylines = false });
        Assert.Equal("[404]", await PageAsync(c, "/author/anna"));
    }

    [Fact]
    public async Task A_removed_writer_keeps_their_page_and_their_linked_name()
    {
        await using var c = await PeoplesAgentsTests.Crew.StartAsync();
        var post = await AnnaPublishesAsync(c, "Coimbra");
        var annaId = await c.AccountIdAsync("Anna");
        await c.Owner.SendAsync(HttpMethod.Delete, $"/api/blogs/{c.Blog}/people/{annaId}", csrf: true);

        Assert.Contains("Coimbra", await PageAsync(c, "/author/anna"));
        Assert.Contains("href=\"https://blog.test/author/anna\">Anna</a>", await PageAsync(c, $"/posts/{post["slug"]}"));
    }

    [Theory]
    [InlineData("paper")]
    [InlineData("studio")]
    [InlineData("mono")]
    [InlineData("ledger")]
    public async Task The_author_page_renders_in_every_theme(string theme)
    {
        await using var c = await PeoplesAgentsTests.Crew.StartAsync();
        await AnnaPublishesAsync(c, "Braga");
        await c.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{c.Blog}", csrf: true, body: new { theme });
        var page = await PageAsync(c, "/author/anna");
        Assert.Contains($"/assets/themes/{theme}.css", page);
        Assert.Contains("<h1>Anna</h1>", page);
        Assert.Contains("Braga", page);
    }
}
