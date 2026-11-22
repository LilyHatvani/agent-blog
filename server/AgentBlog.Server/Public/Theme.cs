using System.Text.Encodings.Web;
using Fluid;
using Fluid.Values;

namespace AgentBlog.Public;

/// <summary>
/// Renders the public pages from Liquid with Fluid (decision 0006). The sandbox: a template sees only the model the
/// server builds (plain dictionaries and lists, never a .NET object, so no member of anything leaks), output is HTML-escaped
/// unless a template marks a value raw (only the sanitized post body is), and every render is bounded in steps,
/// recursion, output size and collection size. A template that fails to render falls back to a bare page and is logged,
/// so a broken theme never takes the blog down.
/// </summary>
public sealed class Theme
{
    private static readonly FluidParser Parser = new();

    private readonly TemplateOptions _options = new TemplateOptionsBuilder()
        .WithMaxSteps(100_000)
        .WithMaxRecursion(20)
        .WithMaxOutputSize(4 * 1024 * 1024)
        .WithMaxCollectionSize(10_000)
        .Build();

    private readonly Dictionary<string, IFluidTemplate> _templates = new(StringComparer.Ordinal);
    private readonly ILogger<Theme> _log;

    public string Css { get; }

    public Theme(ILogger<Theme> log)
    {
        _log = log;
        foreach (var name in new[] { "layout", "home", "post", "not-found" })
        {
            var source = Read(name + ".liquid");
            if (!Parser.TryParse(source, out var template, out var error))
                throw new InvalidOperationException($"The default theme's {name}.liquid doesn't parse: {error}");
            _templates[name] = template;
        }
        Css = Read("theme.css");
    }

    /// <summary>Renders <paramref name="page"/> into the layout. The model's "content" is set by this method.</summary>
    public async Task<string> RenderAsync(string page, Dictionary<string, object?> model)
    {
        try
        {
            // The page's own HTML; the layout prints it with | raw, the only other raw value besides the sanitized post body.
            model["content"] = await RenderOneAsync(page, model);
            return await RenderOneAsync("layout", model);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "The theme failed to render {Page}; serving the bare fallback.", page);
            return Fallback(model);
        }
    }

    private async Task<string> RenderOneAsync(string name, Dictionary<string, object?> model)
    {
        var context = new TemplateContext(_options);
        foreach (var (key, value) in model) context.SetValue(key, value ?? NilValue.Instance);
        return await _templates[name].RenderAsync(context, HtmlEncoder.Default);
    }

    /// <summary>A bare page that needs no template: the blog's name, and a way back.</summary>
    private static string Fallback(Dictionary<string, object?> model)
    {
        var blog = model.GetValueOrDefault("blog") as Dictionary<string, object?>;
        var title = HtmlEncoder.Default.Encode(blog?.GetValueOrDefault("title") as string ?? "Blog");
        var url = HtmlEncoder.Default.Encode(blog?.GetValueOrDefault("url") as string ?? "/");
        return $"""<!doctype html><html><head><meta charset="utf-8"><title>{title}</title></head><body><p><a href="{url}">{title}</a></p><p>This page can't be shown right now.</p></body></html>""";
    }

    private static string Read(string file)
    {
        var asm = typeof(Theme).Assembly;
        using var stream = asm.GetManifestResourceStream("theme/" + file)
            ?? throw new InvalidOperationException($"The default theme's {file} isn't embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
