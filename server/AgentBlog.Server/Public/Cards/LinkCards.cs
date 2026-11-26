using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace AgentBlog.Public.Cards;

/// <summary>What a link card says: the blog, the title, and a line under it (a date, or the blog's description).</summary>
public sealed record CardText(string Blog, string Title, string Line, string Host)
{
    /// <summary>A short fingerprint of everything drawn, in the card's address, so a changed title is a new address and
    /// every platform that cached the old picture fetches the new one. Drawing goes up when a theme's card is drawn
    /// differently (v2: Midnight got its own card; until then it drew as Paper under the same address).</summary>
    public string Version(string theme) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{Drawing}\n{theme}\n{Blog}\n{Title}\n{Line}\n{Host}")))[..12];

    private const string Drawing = "v2";
}

/// <summary>
/// The picture a shared link shows when a post has none of its own (docs/specs/social.md § Link cards): 1200 × 630 PNG,
/// drawn on the server in the blog's theme with SkiaSharp (MIT) from fonts embedded in it (Public/Cards/Fonts).
/// Deterministic: the same text in the same theme gives the same picture. Rendered cards are kept in memory by their
/// fingerprint. A letter a theme's font lacks (Greek, Cyrillic, …) switches the text to DejaVu Sans, which has it.
/// </summary>
public sealed class LinkCards : IDisposable
{
    public const int Width = 1200;
    public const int Height = 630;

    private readonly SKTypeface _interBold, _inter, _monoBold, _mono, _serifBold, _serif, _fallback;
    private readonly Dictionary<string, byte[]> _cache = [];
    private readonly Queue<string> _order = new();
    private readonly Lock _gate = new();

    public LinkCards()
    {
        static SKTypeface Load(string file)
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"cards/{file}")
                ?? throw new InvalidOperationException($"The card font {file} isn't embedded.");
            return SKTypeface.FromStream(s) ?? throw new InvalidOperationException($"The card font {file} doesn't load.");
        }
        _interBold = Load("Inter-Bold.otf");
        _inter = Load("Inter-Regular.otf");
        _monoBold = Load("JetBrainsMono-Bold.ttf");
        _mono = Load("JetBrainsMono-Regular.ttf");
        _serifBold = Load("DejaVuSerif-Bold.ttf");
        _serif = Load("DejaVuSerif.ttf");
        _fallback = Load("DejaVuSans.ttf");
    }

    /// <summary>The PNG for <paramref name="text"/> in <paramref name="theme"/> (an unknown theme draws as Paper).</summary>
    public byte[] Render(string theme, CardText text)
    {
        var key = text.Version(theme);
        lock (_gate)
            if (_cache.TryGetValue(key, out var hit)) return hit;

        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        switch (theme)
        {
            case "ledger": Ledger(c, text); break;
            case "mono": Mono(c, text); break;
            case "studio": Studio(c, text); break;
            case "midnight": Midnight(c, text); break;
            default: Paper(c, text); break;
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        lock (_gate)
        {
            if (_cache.TryAdd(key, bytes)) _order.Enqueue(key);
            while (_order.Count > 200) _cache.Remove(_order.Dequeue());
        }
        return bytes;
    }

    // ---------------------------------------------------------------------------------------------- the themes

    private void Paper(SKCanvas c, CardText t)
    {
        var fg = SKColor.Parse("#1f1d1a");
        var muted = SKColor.Parse("#6d675e");
        c.Clear(SKColor.Parse("#fdfcf9"));
        Rect(c, SKColor.Parse("#3d5a80"), 0, 0, 16, Height);
        Line(c, _interBold, 34, t.Blog, fg, 96, 100, 1000, center: false);
        Title(c, _interBold, t.Title, fg, new SKRect(96, 160, 1106, 490), 76, 40, center: false);
        Line(c, _inter, 28, Join(t.Line, t.Host), muted, 96, 560, 1000, center: false);
    }

    private void Ledger(SKCanvas c, CardText t)
    {
        var fg = SKColor.Parse("#1b1a17");
        var muted = SKColor.Parse("#5f5a50");
        c.Clear(SKColor.Parse("#f7f3ea"));
        Line(c, _serifBold, 40, t.Blog.ToUpperInvariant(), fg, Width / 2f, 100, Width - 160, center: true);
        Rect(c, fg, 80, 130, Width - 160, 3);
        Rect(c, fg, 80, 139, Width - 160, 1);
        Title(c, _serifBold, t.Title, fg, new SKRect(100, 180, Width - 100, 490), 70, 38, center: true);
        Rect(c, fg, 80, 505, Width - 160, 1);
        Line(c, _serif, 26, Join(t.Line, t.Host).ToUpperInvariant(), muted, Width / 2f, 560, Width - 160, center: true);
    }

    private void Mono(SKCanvas c, CardText t)
    {
        var fg = SKColor.Parse("#d7dad0");
        var muted = SKColor.Parse("#7f877c");
        c.Clear(SKColor.Parse("#0e1013"));
        Line(c, _mono, 30, $"~/{Slug(t.Blog)} $ cat post", SKColor.Parse("#e6c07b"), 80, 100, 1040, center: false);
        // The prompt's "#" sits on the title's first line, at the title's size, however large that came out.
        var (size, baseline) = Title(c, _monoBold, t.Title, fg, new SKRect(140, 170, 1120, 490), 60, 34, center: false);
        Line(c, _monoBold, size, "#", muted, 80, baseline, 60, center: false);
        Line(c, _mono, 26, Join(t.Line, t.Host), muted, 80, 560, 1040, center: false);
        Rect(c, SKColor.Parse("#8be28b"), 80, 585, 22, 4);
    }

    private void Studio(SKCanvas c, CardText t)
    {
        using (var paint = new SKPaint())
        {
            paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(Width, Height),
                [SKColor.Parse("#4b3fe0"), SKColor.Parse("#c2349b")], SKShaderTileMode.Clamp);
            c.DrawRect(0, 0, Width, Height, paint);
        }
        Line(c, _interBold, 36, t.Blog, SKColors.White, 80, 105, 1040, center: false);
        Title(c, _interBold, t.Title, SKColors.White, new SKRect(80, 165, 1120, 490), 88, 44, center: false);
        Line(c, _inter, 28, Join(t.Line, t.Host), SKColors.White.WithAlpha(204), 80, 560, 1040, center: false);
    }

    /// <summary>
    /// Midnight, Echo of Dreams' look: the deep night background with the violet glow the pages have, the blog's name in
    /// the accent, the title in a serif (the renderer's; the pages use Cormorant Garamond, which ships only as woff2).
    /// </summary>
    private void Midnight(SKCanvas c, CardText t)
    {
        var fg = SKColor.Parse("#faf5ff");
        var muted = SKColor.Parse("#bcb6d8");
        var accent = SKColor.Parse("#c084fc");
        c.Clear(SKColor.Parse("#050b16"));
        using (var paint = new SKPaint { IsAntialias = true })
        {
            paint.Shader = SKShader.CreateRadialGradient(new SKPoint(260, -40), 900,
                [SKColor.Parse("#2b1650"), SKColor.Parse("#050b16").WithAlpha(0)], SKShaderTileMode.Clamp);
            c.DrawRect(0, 0, Width, Height, paint);
        }
        Line(c, _interBold, 28, t.Blog.ToUpperInvariant(), accent, 96, 110, 1000, center: false);
        Title(c, _serifBold, t.Title, fg, new SKRect(96, 165, 1104, 490), 76, 40, center: false);
        Rect(c, accent, 96, 525, 72, 4);
        Line(c, _inter, 28, Join(t.Line, t.Host), muted, 96, 575, 1000, center: false);
    }

    // ---------------------------------------------------------------------------------------------- drawing

    private static void Rect(SKCanvas c, SKColor color, float x, float y, float w, float h)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        c.DrawRect(x, y, w, h, paint);
    }

    /// <summary>The theme's face, or DejaVu Sans when the text has a letter the face doesn't.</summary>
    private SKTypeface Face(SKTypeface face, string text)
    {
        using var font = new SKFont(face);
        return font.ContainsGlyphs(text) ? face : _fallback;
    }

    /// <summary>One line on a baseline, cut with an ellipsis to fit <paramref name="width"/>.</summary>
    private void Line(SKCanvas c, SKTypeface face, float size, string text, SKColor color, float x, float baseline, float width, bool center)
    {
        if (text.Length == 0) return;
        using var font = new SKFont(Face(face, text), size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        var fitted = Fit(text, font, width);
        c.DrawText(fitted, x, baseline, center ? SKTextAlign.Center : SKTextAlign.Left, font, paint);
    }

    /// <summary>
    /// The title as large as it fits in <paramref name="box"/>: from <paramref name="max"/> down to
    /// <paramref name="min"/> points, wrapped at spaces; at the smallest size a title that still doesn't fit loses words
    /// from the end and gains an ellipsis.
    /// </summary>
    /// <returns>The size the title came out at and its first line's baseline.</returns>
    private (float Size, float Baseline) Title(SKCanvas c, SKTypeface face, string title, SKColor color, SKRect box, float max, float min, bool center)
    {
        title = title.ReplaceLineEndings(" ").Trim();
        face = Face(face, title);
        List<string>? lines = null;
        var size = max;
        for (; size >= min; size -= 4)
        {
            using var f = new SKFont(face, size);
            var wrapped = Wrap(title, f, box.Width);
            if (wrapped.Count * size * LineHeight <= box.Height) { lines = wrapped; break; }
        }
        if (lines is null)
        {
            size = min;
            using var f = new SKFont(face, size);
            var fits = (int)(box.Height / (size * LineHeight));
            lines = Wrap(title, f, box.Width);
            if (lines.Count > fits)
            {
                lines = lines[..fits];
                lines[^1] = Fit(lines[^1] + " …", f, box.Width).Replace(" …", "…", StringComparison.Ordinal);
                if (!lines[^1].EndsWith('…')) lines[^1] = Fit(lines[^1] + "…", f, box.Width);
            }
        }
        using var font = new SKFont(face, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        var first = box.Top + size;
        var y = first;
        foreach (var line in lines)
        {
            c.DrawText(line, center ? box.MidX : box.Left, y, center ? SKTextAlign.Center : SKTextAlign.Left, font, paint);
            y += size * LineHeight;
        }
        return (size, first);
    }

    private const float LineHeight = 1.15f;

    private static List<string> Wrap(string text, SKFont font, float width)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(candidate) <= width || current.Length == 0)
            {
                // A single word longer than the line is cut to fit rather than overflowing the card.
                current = font.MeasureText(candidate) <= width ? candidate : Fit(candidate, font, width);
            }
            else
            {
                lines.Add(current);
                current = font.MeasureText(word) <= width ? word : Fit(word, font, width);
            }
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    private static string Fit(string text, SKFont font, float width)
    {
        if (font.MeasureText(text) <= width) return text;
        for (var n = text.Length - 1; n > 1; n--)
        {
            var cut = AgentBlog.Posts.XmlText.Cut(text, n).TrimEnd() + "…";
            if (font.MeasureText(cut) <= width) return cut;
        }
        return "…";
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : $"{a}  ·  {b}";

    private static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        return sb.ToString().Trim('-') is { Length: > 0 } slug ? slug : "blog";
    }

    public void Dispose()
    {
        foreach (var f in new[] { _interBold, _inter, _monoBold, _mono, _serifBold, _serif, _fallback }) f.Dispose();
    }
}
