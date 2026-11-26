using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dapper;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Sections (docs/specs/taxonomy.md § Sections, public-site.md): a category the owner marks as a section of the blog,
/// in settings and over MCP at Full; its landing page and feeds, the site nav in every theme, the home page's filter,
/// and the sitemap. With no section the home page is byte-identical to before.
/// </summary>
public sealed class SectionTests
{
    private static string Id(JsonNode post) => post["id"]!.GetValue<string>();

    private static async Task<string> BlogIdAsync(PostTests.World w) =>
        (await w.Owner.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();

    private static async Task<string> ReaderAsync(PostTests.World w, string path)
    {
        var r = await w.Host.Browser().Http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadAsStringAsync();
    }

    /// <summary>A live post in the given category (created at Publish), returns its slug.</summary>
    private static async Task<string> LiveInAsync(PostTests.World w, string title, string category)
    {
        await w.SetLevelAsync("publish");
        var post = (await w.OkAsync("create_draft", new { title, body_md = "Words.", categories = new[] { category } }))["post"]!;
        await w.OkAsync("publish_post", new { post = Id(post) });
        return post["slug"]!.GetValue<string>();
    }

    private static Task<JsonResult> MarkAsync(PostTests.World w, string blog, string slug, object body) =>
        w.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{blog}/categories/{slug}", csrf: true, body);

    [Fact]
    public async Task The_owner_marks_a_category_as_a_section_and_readers_get_a_landing_page_feeds_and_nav()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogIdAsync(w);
        var slug = await LiveInAsync(w, "A guide", "Guides");
        await LiveInAsync(w, "Another guide", "Guides");

        // A non-section category page is the unchanged term page: no intro, no section feeds.
        var before = await ReaderAsync(w, "/category/guides");
        Assert.DoesNotContain("section-intro", before);
        Assert.DoesNotContain("/category/guides/feed.xml", before);

        var marked = await MarkAsync(w, blog, "guides", new
        {
            is_section = true, title = "The Guides", intro = "Everything we **learned**.", seo_title = "Guides SEO",
            meta_description = "How to do the thing.", nav_order = 1, show_on_home = true,
        });
        Assert.Equal(HttpStatusCode.OK, marked.Status);
        Assert.Equal("The Guides", marked.Json!["category"]!["section"]!["title"]!.GetValue<string>());

        var page = await ReaderAsync(w, "/category/guides");
        Assert.Contains("<h1>The Guides</h1>", page);
        Assert.Contains("Everything we <strong>learned</strong>.", page); // the intro, Markdown rendered
        Assert.Contains("A guide", page);
        Assert.Contains("Another guide", page);
        // Its feeds are advertised, and they carry only its posts.
        Assert.Contains("<link rel=\"alternate\" type=\"application/rss+xml\" title=\"Guides SEO &#xB7; My blog\" href=\"https://blog.test/category/guides/feed.xml\">", page);
        Assert.Contains("href=\"https://blog.test/category/guides/atom.xml\"", page);
        Assert.Contains($"<link rel=\"canonical\" href=\"https://blog.test/category/guides\">", page);
        Assert.Contains("<meta name=\"description\" content=\"How to do the thing.\">", page);

        var rss = XDocument.Parse(await ReaderAsync(w, "/category/guides/feed.xml"));
        Assert.Equal("The Guides", rss.Root!.Element("channel")!.Element("title")!.Value);
        Assert.Equal(2, rss.Descendants("item").Count());
        var atom = XDocument.Parse(await ReaderAsync(w, "/category/guides/atom.xml"));
        XNamespace a = "http://www.w3.org/2005/Atom";
        Assert.Equal(2, atom.Descendants(a + "entry").Count());
        Assert.Equal("https://blog.test/category/guides/atom.xml",
            atom.Root!.Elements(a + "link").Single(l => l.Attribute("rel")!.Value == "self").Attribute("href")!.Value);
    }

    [Fact]
    public async Task A_plain_category_has_no_section_feed_and_a_section_page_exists_even_before_any_post_is_live()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogIdAsync(w);
        await w.SetLevelAsync("publish");
        await w.OkAsync("create_category", new { name = "Notes" });
        await MarkAsync(w, blog, "notes", new { is_section = true, intro = "Coming soon." });

        // No live post yet, but the section's intro is public: the page renders, and it is in the sitemap.
        var page = await ReaderAsync(w, "/category/notes");
        Assert.Contains("Coming soon.", page);
        Assert.Contains("https://blog.test/category/notes", await ReaderAsync(w, "/sitemap.xml"));

        // A plain category keeps the old rule: no live posts, no page; and no section feed.
        await w.OkAsync("create_category", new { name = "Plain" });
        Assert.Equal(HttpStatusCode.NotFound, (await w.Host.Browser().Http.GetAsync("/category/plain")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Host.Browser().Http.GetAsync("/category/plain/feed.xml")).StatusCode);
    }

    [Fact]
    public async Task Every_section_is_in_the_site_nav_in_every_theme_in_nav_order()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogIdAsync(w);
        await LiveInAsync(w, "Personal post", "Personal");
        await LiveInAsync(w, "Publication post", "Publication");
        await MarkAsync(w, blog, "publication", new { is_section = true, title = "Publication", nav_order = 1 });
        await MarkAsync(w, blog, "personal", new { is_section = true, title = "Personal", nav_order = 2 });

        foreach (var theme in new[] { "paper", "ledger", "mono", "studio", "midnight" })
        {
            await w.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{blog}", csrf: true, body: new { theme });
            var home = await ReaderAsync(w, "/");
            Assert.Contains("class=\"site-sections\"", home);
            var nav = System.Text.RegularExpressions.Regex.Match(home, "<nav class=\"site-sections\"[\\s\\S]*?</nav>").Value;
            Assert.Contains("href=\"https://blog.test/category/publication\">Publication</a>", nav);
            Assert.Contains("href=\"https://blog.test/category/personal\">Personal</a>", nav);
            Assert.True(nav.IndexOf("publication", StringComparison.Ordinal) < nav.IndexOf("personal", StringComparison.Ordinal), theme);
            Assert.DoesNotContain("aria-current", nav); // the home page is no section

            // On a section's own page the nav marks that section as the current one, and only that one.
            var onPersonal = System.Text.RegularExpressions.Regex.Match(await ReaderAsync(w, "/category/personal"),
                "<nav class=\"site-sections\"[\\s\\S]*?</nav>").Value;
            Assert.Contains("<a aria-current=\"page\" href=\"https://blog.test/category/personal\">Personal</a>", onPersonal);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(onPersonal, "aria-current"));
        }
    }

    [Fact]
    public async Task The_home_page_lists_sections_kept_on_it_plus_posts_in_no_section_and_is_unchanged_with_no_section()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogIdAsync(w);
        await w.SetLevelAsync("publish");
        var loose = (await w.OkAsync("create_draft", new { title = "Loose post", body_md = "x" }))["post"]!;
        await w.OkAsync("publish_post", new { post = Id(loose) });
        var shown = (await w.OkAsync("create_draft", new { title = "Shown section post", body_md = "x", categories = new[] { "Shown" } }))["post"]!;
        await w.OkAsync("publish_post", new { post = Id(shown) });
        var hidden = (await w.OkAsync("create_draft", new { title = "Hidden section post", body_md = "x", categories = new[] { "Hidden" } }))["post"]!;
        await w.OkAsync("publish_post", new { post = Id(hidden) });

        // No section yet: every live post shows, and there is no nav. This is the page as it was before sections.
        var baseline = await ReaderAsync(w, "/");
        Assert.Contains("Loose post", baseline);
        Assert.Contains("Shown section post", baseline);
        Assert.Contains("Hidden section post", baseline);
        Assert.DoesNotContain("site-sections", baseline);

        await MarkAsync(w, blog, "shown", new { is_section = true, show_on_home = true });
        await MarkAsync(w, blog, "hidden", new { is_section = true, show_on_home = false });

        var home = await ReaderAsync(w, "/");
        Assert.Contains("Loose post", home);          // in no section
        Assert.Contains("Shown section post", home);   // a section kept on the home page
        Assert.DoesNotContain("Hidden section post", home); // a section kept off it

        // Unmark both: the home page is byte-identical to the baseline again.
        await MarkAsync(w, blog, "shown", new { is_section = false });
        await MarkAsync(w, blog, "hidden", new { is_section = false });
        Assert.Equal(baseline, await ReaderAsync(w, "/"));
    }

    [Fact]
    public async Task An_agent_at_Full_edits_a_category_over_MCP_and_below_Full_it_waits_for_the_owner()
    {
        await using var w = await PostTests.World.StartAsync();
        await w.SetLevelAsync("publish");
        await w.OkAsync("create_category", new { name = "Guides" });

        // Below Full: a request, pinned to the category as it stands.
        await w.SetLevelAsync("publish");
        var asked = await w.OkAsync("edit_category", new { category = "guides", is_section = true, title = "Guides" });
        Assert.Equal("waiting_for_owner", asked["status"]!.GetValue<string>());
        var request = asked["request"]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await w.OwnerPostAsync($"/api/blog-requests/{request}/approve")).Status);
        await using (var conn = new NpgsqlConnection(w.Host.App.AppConnectionString))
            Assert.NotNull(await conn.QuerySingleAsync<string?>("SELECT section::text FROM term WHERE slug = 'guides'"));
        Assert.Contains("site-sections", await ReaderAsync(w, "/"));

        // At Full: done at once.
        await w.SetLevelAsync("full");
        var done = await w.OkAsync("edit_category", new { category = "guides", meta_description = "All the guides." });
        Assert.Equal("done", done["status"]!.GetValue<string>());
        Assert.Contains("All the guides.", await ReaderAsync(w, "/category/guides"));

        // A tag is never a section, and an unknown category is refused.
        var (refused, body) = await w.ToolAsync("edit_category", new { category = "nope", is_section = true });
        Assert.True(refused);
        Assert.Equal("not_found", body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_writer_puts_a_post_in_a_section_with_create_draft_and_edit_post_as_any_category()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogIdAsync(w);
        await w.SetLevelAsync("publish");
        await w.OkAsync("create_category", new { name = "Guides" });
        await MarkAsync(w, blog, "guides", new { is_section = true, title = "The Guides" });

        // A section is a category: create_draft puts a post in it by name, and it shows on the section page and feed.
        var post = (await w.OkAsync("create_draft", new { title = "Drafted into the section", body_md = "x", categories = new[] { "Guides" } }))["post"]!;
        Assert.Equal(["Guides"], [.. post["categories"]!.AsArray().Select(c => c!.GetValue<string>())]);
        await w.OkAsync("publish_post", new { post = Id(post) });
        Assert.Contains("Drafted into the section", await ReaderAsync(w, "/category/guides"));
        Assert.Contains("Drafted into the section", await ReaderAsync(w, "/category/guides/feed.xml"));

        // edit_post moves a post into the section by name, the same way.
        var other = (await w.OkAsync("create_draft", new { title = "Moved in later", body_md = "x" }))["post"]!;
        await w.OkAsync("publish_post", new { post = Id(other) });
        Assert.DoesNotContain("Moved in later", await ReaderAsync(w, "/category/guides"));
        await w.OkAsync("edit_post", new { post = Id(other), base_revision = other["revision"]!.GetValue<string>(), categories = new[] { "Guides" } });
        Assert.Contains("Moved in later", await ReaderAsync(w, "/category/guides"));
    }

    [Fact]
    public async Task A_section_edit_request_goes_stale_when_the_category_changed_since()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogIdAsync(w);
        await w.SetLevelAsync("publish");
        await w.OkAsync("create_category", new { name = "Guides" });
        var asked = await w.OkAsync("edit_category", new { category = "guides", is_section = true, title = "From the agent" });
        var request = asked["request"]!["id"]!.GetValue<string>();

        // The owner renames it first: the agent's request no longer applies.
        await MarkAsync(w, blog, "guides", new { description = "Changed by the owner." });
        var decided = await w.Owner.PostJsonAsync($"/api/blog-requests/{request}/approve", null, csrf: true);
        Assert.Equal(HttpStatusCode.Conflict, decided.Status);
        Assert.Equal("stale", decided.Json!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_malformed_section_mark_never_takes_the_site_down_and_the_owner_can_unmark_it()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogIdAsync(w);
        await LiveInAsync(w, "A guide", "Guides");
        Assert.Equal(HttpStatusCode.OK, (await MarkAsync(w, blog, "guides", new { is_section = true, title = "The Guides" })).Status);

        // A mark the API would never write, stored directly: nav_order a string. Every public page reads the sections
        // (the nav), so one bad mark used to 500 the whole site — and the owner could not unmark it.
        await using (var conn = new NpgsqlConnection(w.Host.App.AppConnectionString))
            await conn.ExecuteAsync("UPDATE term SET section = '{\"nav_order\":\"x\"}'::jsonb WHERE blog_id = @b::uuid AND slug = 'guides'", new { b = blog });

        Assert.Contains("site-sections", await ReaderAsync(w, "/"));
        Assert.Contains("Guides", await ReaderAsync(w, "/category/guides")); // the title falls back to the name
        // The owner can still unmark it: the tolerant read gives defaults, and the edit writes null.
        Assert.Equal(HttpStatusCode.OK, (await MarkAsync(w, blog, "guides", new { is_section = false })).Status);
        Assert.DoesNotContain("site-sections", await ReaderAsync(w, "/"));
    }

    [Fact]
    public async Task A_character_XML_cant_carry_in_a_category_or_section_field_is_refused_naming_it()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogIdAsync(w);
        await w.SetLevelAsync("publish");
        await w.OkAsync("create_category", new { name = "Guides" });

        // Every field a reader or a feed could carry: refused, naming the field, never stored (U+0001 breaks XML).
        var bad = "Bad" + (char)1;
        foreach (var field in new[] { "title", "intro", "seo_title", "meta_description", "name", "description" })
        {
            var refused = await w.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{blog}/categories/guides", csrf: true,
                body: new Dictionary<string, object> { [field] = bad });
            Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
            Assert.Equal("invalid_argument", refused.Json!["code"]!.GetValue<string>());
            Assert.Contains("U+0001, a character XML can't carry", refused.Json!["detail"]!.GetValue<string>());
        }

        // The same core over MCP: a Full agent's edit_category is refused with the reason too.
        await w.SetLevelAsync("full");
        var (mcpRefused, body) = await w.ToolAsync("edit_category", new { category = "guides", title = bad });
        Assert.True(mcpRefused);
        Assert.Equal("invalid_argument", body["code"]!.GetValue<string>());
        Assert.Contains("U+0001, a character XML can't carry", body["message"]!.GetValue<string>());
    }

    private static async Task<(string Description, string Intro)> StoredAsync(PostTests.World w, string slug)
    {
        await using var conn = new NpgsqlConnection(w.Host.App.AppConnectionString);
        return await conn.QuerySingleAsync<(string, string)>(
            "SELECT description, coalesce(section->>'intro', '') FROM term WHERE slug = @slug", new { slug });
    }

    /// <summary>The paragraphs of the section page's intro.</summary>
    private static int IntroParagraphs(string page)
    {
        var intro = System.Text.RegularExpressions.Regex.Match(page, "<div class=\"section-intro\">(.*?)</div>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(intro.Success, "the section page has an intro");
        return System.Text.RegularExpressions.Regex.Matches(intro.Groups[1].Value, "<p>").Count;
    }

    [Fact]
    public async Task A_multi_line_intro_and_description_are_saved_as_written_and_render_as_paragraphs()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogIdAsync(w);
        await LiveInAsync(w, "A guide", "Guides");

        // The owner in settings: a Markdown intro of two paragraphs, and a description over two lines. XML carries LF.
        var marked = await MarkAsync(w, blog, "guides", new
        {
            is_section = true, title = "The Guides", intro = "First paragraph.\n\nSecond paragraph.", description = "Line one.\nLine two.",
        });
        Assert.Equal(HttpStatusCode.OK, marked.Status);
        var (description, intro) = await StoredAsync(w, "guides");
        Assert.Equal("First paragraph.\n\nSecond paragraph.", intro);
        Assert.Equal("Line one.\nLine two.", description);
        Assert.Equal(2, IntroParagraphs(await ReaderAsync(w, "/category/guides")));
        foreach (var feed in new[] { "/category/guides/feed.xml", "/category/guides/atom.xml", "/feed.xml", "/sitemap.xml" })
            XDocument.Parse(await ReaderAsync(w, feed));

        // The same core over MCP: a Full agent's edit_category keeps the paragraphs too.
        await w.SetLevelAsync("full");
        var done = await w.OkAsync("edit_category", new
        {
            category = "guides", intro = "From the agent.\n\nIn two parts.\n\nNo, three.", description = "Agent's line one.\r\nAnd two.",
        });
        Assert.Equal("done", done["status"]!.GetValue<string>());
        (description, intro) = await StoredAsync(w, "guides");
        Assert.Equal("From the agent.\n\nIn two parts.\n\nNo, three.", intro);
        Assert.Equal("Agent's line one.\r\nAnd two.", description);
        Assert.Equal(3, IntroParagraphs(await ReaderAsync(w, "/category/guides")));
        XDocument.Parse(await ReaderAsync(w, "/category/guides/feed.xml"));
        XDocument.Parse(await ReaderAsync(w, "/category/guides/atom.xml"));
    }

    [Fact]
    public async Task Prose_fields_keep_tabs_and_line_breaks_and_one_line_fields_keep_every_character_XML_carries()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogIdAsync(w);
        await w.SetLevelAsync("publish");
        await w.OkAsync("create_category", new { name = "Guides" });

        // Tab, LF and CR: XML carries each, so the prose fields keep them.
        foreach (var (field, value) in new[] { ("intro", "a\tb"), ("intro", "a\nb"), ("intro", "a\rb"), ("description", "a\tb"),
                     ("description", "a\nb"), ("description", "a\rb"), ("meta_description", "a\nb") })
            Assert.Equal(HttpStatusCode.OK, (await MarkAsync(w, blog, "guides",
                new Dictionary<string, object> { ["is_section"] = true, [field] = value })).Status);

        // A name or a title is one line: a tab or a line break is refused for that reason, not XML's.
        foreach (var field in new[] { "name", "title", "seo_title" })
            foreach (var value in new[] { "a\tb", "a\nb", "a\rb" })
            {
                var refused = await MarkAsync(w, blog, "guides", new Dictionary<string, object> { [field] = value });
                Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
                Assert.Equal($"{field} is one line: take out the tab or line break.", refused.Json!["detail"]!.GetValue<string>());
            }

        // Every other character XML carries is the owner's to use: a right-to-left mark, an emoji ZWJ sequence.
        var rlm = "Guides " + (char)0x200F + "عربى";
        var family = "Family " + char.ConvertFromUtf32(0x1F468) + (char)0x200D + char.ConvertFromUtf32(0x1F469) + (char)0x200D
            + char.ConvertFromUtf32(0x1F467);
        foreach (var (field, value) in new[] { ("title", rlm), ("title", family), ("seo_title", family), ("name", rlm), ("name", family) })
        {
            var ok = await MarkAsync(w, blog, "guides", new Dictionary<string, object> { [field] = value });
            Assert.Equal(HttpStatusCode.OK, ok.Status);
        }
        var page = await ReaderAsync(w, "/category/guides");
        Assert.Contains(family, WebUtility.HtmlDecode(page), StringComparison.Ordinal); // the theme writes non-ASCII as references
        var rss = XDocument.Parse(await ReaderAsync(w, "/category/guides/feed.xml"));
        Assert.Equal(family, rss.Root!.Element("channel")!.Element("title")!.Value);
    }

    [Fact]
    public async Task A_stored_control_character_never_breaks_the_feeds()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogIdAsync(w);
        var slug = await LiveInAsync(w, "A guide", "Guides");
        Assert.Equal(HttpStatusCode.OK, (await MarkAsync(w, blog, "guides", new { is_section = true, title = "The Guides" })).Status);

        // Store what the refusals now keep out (a row from before them): U+0001 in the post's title and the section's.
        // The feeds drop what XML can't carry and still answer. Revisions are append-only, so the bad title arrives as
        // a new revision the post points at; the owner role writes it (the app may not).
        var bad = ((char)1).ToString();
        var revision = Guid.CreateVersion7();
        await using (var conn = new NpgsqlConnection(w.Host.App.OwnerConnectionString))
        {
            await conn.ExecuteAsync("""
                INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, excerpt, actor, created_at, term_ids)
                SELECT @revision, blog_id, post_id, number + 1, title || @c, body_md, excerpt, actor, now(), term_ids
                FROM post_revision WHERE id = (SELECT current_revision_id FROM post WHERE blog_id = @b::uuid AND slug = @slug);
                UPDATE post SET current_revision_id = @revision WHERE blog_id = @b::uuid AND slug = @slug;
                UPDATE term SET section = jsonb_set(section, '{title}', to_jsonb((section->>'title') || @c)) WHERE blog_id = @b::uuid AND slug = 'guides';
                """, new { revision, b = blog, slug, c = bad });
        }

        foreach (var path in new[] { "/feed.xml", "/atom.xml", "/category/guides/feed.xml", "/category/guides/atom.xml" })
        {
            var r = await w.Host.Browser().Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var xml = await r.Content.ReadAsStringAsync();
            // Ordinal: a culture-sensitive search treats U+0001 as ignorable and "finds" it in any string.
            Assert.DoesNotContain(bad, xml, StringComparison.Ordinal);
            XDocument.Parse(xml); // a control character that survived would throw here
        }
        var rss = XDocument.Parse(await (await w.Host.Browser().Http.GetAsync("/feed.xml")).Content.ReadAsStringAsync());
        Assert.Equal("A guide", rss.Root!.Element("channel")!.Element("item")!.Element("title")!.Value);
    }

    [Fact]
    public async Task Only_the_owner_edits_a_category_from_the_app_and_a_no_op_writes_no_event()
    {
        await using var w = await PostTests.World.StartAsync();
        var blog = await BlogIdAsync(w);
        await w.SetLevelAsync("publish");
        await w.OkAsync("create_category", new { name = "Guides" });

        Assert.Equal(HttpStatusCode.OK, (await MarkAsync(w, blog, "guides", new { is_section = true })).Status);
        await using var conn = new NpgsqlConnection(w.Host.App.AppConnectionString);
        var events = await conn.QuerySingleAsync<int>("SELECT count(*)::int FROM event WHERE kind = 'term_changed'");
        // Sending the same mark again changes nothing: no new event.
        Assert.Equal(HttpStatusCode.OK, (await MarkAsync(w, blog, "guides", new { is_section = true })).Status);
        Assert.Equal(events, await conn.QuerySingleAsync<int>("SELECT count(*)::int FROM event WHERE kind = 'term_changed'"));

        // A stranger's blog reads as not found, and a write needs the CSRF header.
        Assert.Equal(HttpStatusCode.NotFound, (await MarkAsync(w, Guid.NewGuid().ToString(), "guides", new { is_section = true })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Owner.SendAsync(HttpMethod.Patch, $"/api/blogs/{blog}/categories/guides", csrf: false, body: new { is_section = true })).Status);
    }
}
