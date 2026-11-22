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
        var first = Markdown.Parse(bodyMd).Descendants<LinkInline>()
            .Where(l => l.IsImage && l.Url is not null)
            .Select(l => (Match: MediaPath().Match(l.Url!), Alt: string.Concat(l.Descendants<LiteralInline>().Select(x => x.Content.ToString())).Trim()))
            .FirstOrDefault(x => x.Match.Success);
        if (first.Match is null || !Guid.TryParse(first.Match.Groups[1].Value, out var id)) return null;
        var ready = await db.ReadAsync(tx => tx.One<bool>(
            "SELECT EXISTS (SELECT 1 FROM media WHERE blog_id = @blogId AND id = @id AND status = 'ready' AND filename = @file)",
            new { blogId, id, file = first.Match.Groups[2].Value }), ct);
        return ready ? new ShareImage(new Uri(new Uri(blogUrl), first.Match.Value).AbsoluteUri, first.Alt, null, null) : null;
    }

    /// <summary>The blog's link card for a post, versioned by what it shows.</summary>
    public static ShareImage PostCard(string blogUrl, string theme, string slug, Cards.CardText text) =>
        new($"{blogUrl}cards/posts/{Uri.EscapeDataString(slug)}.png?v={text.Version(theme)}", text.Title, Cards.LinkCards.Width, Cards.LinkCards.Height);

    public static ShareImage BlogCard(string blogUrl, string theme, Cards.CardText text) =>
        new($"{blogUrl}cards/blog.png?v={text.Version(theme)}", text.Title, Cards.LinkCards.Width, Cards.LinkCards.Height);

    public static string Host(string blogUrl) => new Uri(blogUrl).Host;

    public static string Day(DateTime utc) => utc.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    // ----------------------------------------------------------------------------------------- structured data

    /// <summary>
    /// schema.org BlogPosting. The author is the blog until bylines name people and agents (M2 slice 8). Serialized with
    /// the default encoder, which escapes &lt;, &gt; and &amp;, so no title can close the script element it sits in.
    /// </summary>
    public static string PostJsonLd(string blogTitle, string blogUrl, string title, string description, string url, string published,
        string modified, ShareImage image, IReadOnlyList<string> tags, string? section) => Json(new Dictionary<string, object?>
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
        ["author"] = new Dictionary<string, object?> { ["@type"] = "Organization", ["name"] = blogTitle, ["url"] = blogUrl },
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
