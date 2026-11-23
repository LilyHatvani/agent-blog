using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBlog.Database;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AgentBlog.Public;

/// <summary>The picture a shared link shows, and what it shows.</summary>
public sealed record ShareImage(string Url, string Alt, int? Width, int? Height);

/// <summary>
/// What a shared link and a search engine read about a page (docs/specs/social.md): the picture (the post's first own
/// picture, else a link card drawn in the blog's theme) and schema.org structured data. Everything here is built from
/// what readers already see; nothing a reader can't read goes into it.
/// </summary>
public static partial class Social
{
    [GeneratedRegex(@"^/media/([0-9a-fA-F-]{36})/([^/?#]+)$")]
    private static partial Regex MediaPath();

    /// <summary>The first picture in the post that is this blog's own and ready, as an absolute address; null if none.</summary>
    public static async Task<ShareImage?> FirstPictureAsync(Db db, Guid blogId, string bodyMd, string blogUrl, CancellationToken ct)
    {
        // Every picture of this blog's media in the post, in order; the first that is ready wins (one in the trash, or
        // still uploading, is passed over for the next).
        var pictures = Markdown.Parse(bodyMd).Descendants<LinkInline>()
            .Where(l => l.IsImage && l.Url is not null)
            .Select(l => (Match: MediaPath().Match(l.Url!), Alt: string.Concat(l.Descendants<LiteralInline>().Select(x => x.Content.ToString())).Trim()))
            .Where(x => x.Match.Success && Guid.TryParse(x.Match.Groups[1].Value, out _))
            .Take(50)
            .ToList();
        if (pictures.Count == 0) return null;
        var ids = pictures.Select(x => Guid.Parse(x.Match.Groups[1].Value)).Distinct().ToArray();
        var ready = (await db.ReadAsync(tx => tx.Query<(Guid Id, string Filename)>(
            "SELECT id, filename FROM media WHERE blog_id = @blogId AND id = ANY (@ids) AND status = 'ready'", new { blogId, ids }), ct)).ToHashSet();
        foreach (var (match, alt) in pictures)
            if (ready.Contains((Guid.Parse(match.Groups[1].Value), match.Groups[2].Value)))
                return new ShareImage(new Uri(new Uri(blogUrl), match.Value).AbsoluteUri, alt, null, null);
        return null;
    }

    /// <summary>
    /// The picture a post's shared link shows: its own first picture, else its link card drawn in
    /// <paramref name="cardTheme"/>. The one rule for the post page's head and for announcements.
    /// </summary>
    public static async Task<ShareImage> PostImageAsync(Db db, Guid blogId, string blogTitle, string cardTheme, string blogUrl, string slug,
        string title, string bodyMd, DateTime publishedAt, CancellationToken ct) =>
        await FirstPictureAsync(db, blogId, bodyMd, blogUrl, ct)
        ?? PostCard(blogUrl, cardTheme, slug, PostCardText(blogTitle, blogUrl, title, publishedAt));

    public static Cards.CardText PostCardText(string blogTitle, string blogUrl, string title, DateTime publishedAt) =>
        new(blogTitle, title, Day(publishedAt), Host(blogUrl));

    /// <summary>The blog's link card for a post, versioned by what it shows.</summary>
    public static ShareImage PostCard(string blogUrl, string theme, string slug, Cards.CardText text) =>
        new($"{blogUrl}cards/posts/{Uri.EscapeDataString(slug)}.png?v={text.Version(theme)}", text.Title, Cards.LinkCards.Width, Cards.LinkCards.Height);

    public static ShareImage BlogCard(string blogUrl, string theme, Cards.CardText text) =>
        new($"{blogUrl}cards/blog.png?v={text.Version(theme)}", text.Title, Cards.LinkCards.Width, Cards.LinkCards.Height);

    public static string Host(string blogUrl) => new Uri(blogUrl).Host;

    public static string Day(DateTime utc) => utc.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    // ----------------------------------------------------------------------------------------- structured data

    /// <summary>
    /// schema.org BlogPosting. With a byline (docs/specs/bylines.md) the people who wrote or approved it are its
    /// <c>author</c>, a <c>Person</c> each, and agents are <c>contributor</c>, a <c>SoftwareApplication</c> each; a post no
    /// person wrote or approved, or a blog with bylines off, has the blog as author. Serialized with the default encoder,
    /// which escapes &lt;, &gt; and &amp;, so no title or name can close the script element it sits in.
    /// </summary>
    public static string PostJsonLd(string blogTitle, string blogUrl, string title, string description, string url, string published,
        string modified, ShareImage image, IReadOnlyList<string> tags, string? section, Posts.Byline? byline = null)
    {
        var blogAsAuthor = new Dictionary<string, object?> { ["@type"] = "Organization", ["name"] = blogTitle, ["url"] = blogUrl };
        var people = byline?.People.Select(c => new Dictionary<string, object?>
        {
            ["@type"] = "Person", ["name"] = c.Name, ["url"] = Posts.Bylines.AuthorUrl(blogUrl, c.Slug),
        }).ToList() ?? [];
        var agents = byline?.Agents.Select(c => new Dictionary<string, object?> { ["@type"] = "SoftwareApplication", ["name"] = c.Name }).ToList() ?? [];
        return PostJsonLdWith(blogTitle, blogUrl, title, description, url, published, modified, image, tags, section,
            people.Count > 0 ? people : blogAsAuthor, agents.Count > 0 ? agents : null);
    }

    private static string PostJsonLdWith(string blogTitle, string blogUrl, string title, string description, string url, string published,
        string modified, ShareImage image, IReadOnlyList<string> tags, string? section, object author, object? contributor) => Json(new Dictionary<string, object?>
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "BlogPosting",
        ["headline"] = title.Length <= 110 ? title : title[..109] + "…",
        ["description"] = description,
        ["url"] = url,
        ["mainEntityOfPage"] = new Dictionary<string, object?> { ["@type"] = "WebPage", ["@id"] = url },
        ["datePublished"] = published,
        ["dateModified"] = modified,
        ["image"] = new[] { image.Url },
        ["author"] = author,
        ["contributor"] = contributor,
        ["publisher"] = new Dictionary<string, object?> { ["@type"] = "Organization", ["name"] = blogTitle, ["url"] = blogUrl },
        ["isPartOf"] = new Dictionary<string, object?> { ["@type"] = "Blog", ["name"] = blogTitle, ["url"] = blogUrl },
        ["keywords"] = tags.Count > 0 ? string.Join(", ", tags) : null,
        ["articleSection"] = section,
    });

    /// <summary>schema.org Blog, for the front page.</summary>
    public static string BlogJsonLd(string blogTitle, string description, string blogUrl, ShareImage image) => Json(new Dictionary<string, object?>
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "Blog",
        ["name"] = blogTitle,
        ["description"] = description.Length > 0 ? description : null,
        ["url"] = blogUrl,
        ["image"] = image.Url,
    });

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private static string Json(Dictionary<string, object?> value) => JsonSerializer.Serialize(value, JsonOptions);
}
