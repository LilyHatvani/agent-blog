using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AgentBlog.Public;

/// <summary>
/// A post's Markdown to HTML for readers. Agents write posts, so the output is treated as untrusted input made safe:
/// raw HTML in the Markdown is escaped, not passed through, and every link and image keeps only http, https, mailto or a
/// relative address; anything else (javascript:, data:, vbscript:) becomes "#". The result is the one value a theme
/// prints unescaped.
/// </summary>
public static class Markdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .UseFootnotes()
        .DisableHtml()
        .Build();

    /// <summary>The parsed document, with the readers' pipeline: what <c>check_post</c> reads.</summary>
    public static MarkdownDocument Parse(string markdown) => Markdig.Markdown.Parse(markdown, Pipeline);

    /// <param name="absoluteBase">For feeds: root-relative addresses (a picture's <c>/media/…</c>) become absolute on this base,
    /// since a feed reader has no page to resolve them against.</param>
    public static string ToHtml(string markdown, Uri? absoluteBase = null)
    {
        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!SafeUrl(link.Url)) link.Url = "#";
            else if (absoluteBase is not null && link.Url is { } u && u.StartsWith('/') && !u.StartsWith("//", StringComparison.Ordinal))
                link.Url = new Uri(absoluteBase, u).AbsoluteUri;
            if (link.IsImage) link.GetAttributes().AddProperty("loading", "lazy");
        }
        foreach (var auto in document.Descendants<AutolinkInline>())
            if (!SafeUrl(auto.Url)) auto.Url = "#";
        return document.ToHtml(Pipeline);
    }

    /// <summary>Plain text of the Markdown, for descriptions where no excerpt was written.</summary>
    public static string ToPlainText(string markdown, int max)
    {
        var text = Markdig.Markdown.ToPlainText(markdown, Pipeline).ReplaceLineEndings(" ").Trim();
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Length <= max ? text : AgentBlog.Posts.XmlText.Cut(text, max - 1).TrimEnd() + "…";
    }

    internal static bool SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        var u = new string([.. url.Trim().Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c))]);
        var colon = u.IndexOf(':');
        var slash = u.IndexOfAny(['/', '?', '#']);
        // No scheme (a colon after the first / ? #, or none at all) is a relative address.
        if (colon < 0 || (slash >= 0 && slash < colon)) return true;
        var scheme = u[..colon].ToLowerInvariant();
        return scheme is "http" or "https" or "mailto";
    }
}
