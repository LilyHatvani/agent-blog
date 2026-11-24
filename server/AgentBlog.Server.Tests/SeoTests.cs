using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// SEO basics on every post and section page (docs/specs/social.md, public-site.md): canonical on the blog's PublicUrl,
/// a meta description (the post's excerpt, the section's description), Open Graph and Twitter card tags from the /cards
/// images, and Article JSON-LD on posts that no title can break out of — escaped for &lt;/script&gt; and U+2028.
/// </summary>
public sealed class SeoTests
{
    // The JS line separators, built from their code points so the source stays plain ASCII.
    private static readonly string LineSep = ((char)0x2028).ToString();
    private static readonly string ParaSep = ((char)0x2029).ToString();

    private static async Task<string> PageAsync(PostTests.World w, string path)
    {
        var r = await w.Host.Browser().Http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadAsStringAsync();
    }

    private static string? Meta(string html, string property) =>
        Regex.Match(html, $"<meta (?:property|name)=\"{Regex.Escape(property)}\" content=\"([^\"]*)\">") is { Success: true } m
            ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    private static string JsonLdRaw(string html) =>
        Regex.Match(html, "<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;

    private static async Task<string> LiveAsync(PostTests.World w, string title, string? excerpt = null)
    {
        var post = (await w.OkAsync("create_draft", new { title, body_md = "Words.", excerpt }))["post"]!;
        await w.OwnerPostAsync($"/api/posts/{post["id"]}/publish");
        return post["slug"]!.GetValue<string>();
    }

    [Fact]
    public async Task A_post_page_carries_canonical_description_open_graph_twitter_and_article_json_ld()
    {
        await using var w = await PostTests.World.StartAsync();
        var slug = await LiveAsync(w, "A post", "The excerpt.");
        var html = await PageAsync(w, $"/posts/{slug}");

        Assert.Contains($"<link rel=\"canonical\" href=\"https://blog.test/posts/{slug}\">", html);
        Assert.Equal("The excerpt.", Meta(html, "description"));
        Assert.Equal("A post", Meta(html, "og:title"));
        Assert.Equal("The excerpt.", Meta(html, "og:description"));
        Assert.Equal("article", Meta(html, "og:type"));
        Assert.Equal($"https://blog.test/posts/{slug}", Meta(html, "og:url"));
        Assert.Equal("summary_large_image", Meta(html, "twitter:card"));
        Assert.Equal("A post", Meta(html, "twitter:title"));
        Assert.StartsWith("https://blog.test/cards/", Meta(html, "og:image"));
        Assert.Equal(Meta(html, "og:image"), Meta(html, "twitter:image"));

        var ld = JsonNode.Parse(JsonLdRaw(html))!;
        Assert.Equal("BlogPosting", ld["@type"]!.GetValue<string>());
        Assert.Equal("A post", ld["headline"]!.GetValue<string>());
        Assert.NotNull(ld["datePublished"]);
        Assert.NotNull(ld["dateModified"]);
        Assert.NotNull(ld["author"]);
        Assert.Equal(Meta(html, "og:image"), ld["image"]![0]!.GetValue<string>());

        // One canonical, one description: no tag is duplicated.
        Assert.Single(Regex.Matches(html, "<link rel=\"canonical\""));
        Assert.Single(Regex.Matches(html, "<meta name=\"description\""));
    }

    [Fact]
    public async Task A_section_page_carries_canonical_its_description_open_graph_and_twitter()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
        await w.SetLevelAsync("publish");
        var post = (await w.OkAsync("create_draft", new { title = "In the section", body_md = "x", categories = new[] { "Guides" } }))["post"]!;
        await w.OkAsync("publish_post", new { post = post["id"]!.GetValue<string>() });
        await w.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{blog}/categories/guides", csrf: true,
            body: new { is_section = true, title = "The Guides", meta_description = "About the guides." });

        var html = await PageAsync(w, "/category/guides");
        Assert.Contains("<link rel=\"canonical\" href=\"https://blog.test/category/guides\">", html);
        Assert.Equal("About the guides.", Meta(html, "description"));
        Assert.Equal("The Guides · My blog", Meta(html, "og:title"));
        Assert.Equal("About the guides.", Meta(html, "og:description"));
        Assert.Equal("https://blog.test/category/guides", Meta(html, "og:url"));
        Assert.Equal("summary_large_image", Meta(html, "twitter:card"));
        Assert.StartsWith("https://blog.test/cards/", Meta(html, "og:image"));
        Assert.Single(Regex.Matches(html, "<link rel=\"canonical\""));
    }

    [Fact]
    public async Task Article_json_ld_is_escaped_for_closing_script_and_line_separators()
    {
        await using var w = await PostTests.World.StartAsync();
        // A title with a closing script tag and the JS line separators U+2028 / U+2029.
        var title = "Break </script><script>alert(1)</script> out " + LineSep + " and " + ParaSep + " here";
        var slug = await LiveAsync(w, title, "What it's about.");
        var html = await PageAsync(w, $"/posts/{slug}");
        var raw = JsonLdRaw(html);

        // Nothing in the JSON-LD can close the script element it sits in, or carry a raw JS line separator.
        Assert.DoesNotContain("</script>", raw);
        Assert.DoesNotContain(LineSep, raw);
        Assert.DoesNotContain(ParaSep, raw);
        Assert.DoesNotContain("<script>alert(1)", html);

        // …and it still parses back to the very title.
        var ld = JsonNode.Parse(raw)!;
        Assert.Equal(title, ld["headline"]!.GetValue<string>());
    }
}
