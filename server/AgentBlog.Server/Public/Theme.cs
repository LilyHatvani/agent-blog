using System.Text.Encodings.Web;
using System.Text.Json;
using Fluid;
using Fluid.Values;

namespace AgentBlog.Public;

/// <summary>A built-in theme: its name (the address segment), what it's called and what it's like.</summary>
public sealed record ThemeInfo(string Name, string Label, string Description);

/// <summary>
/// Renders the public pages from Liquid with Fluid (decision 0006), in one of the built-in themes
/// (docs/specs/themes.md). A theme is a folder under <c>Public/Themes/</c>: <c>theme.json</c> (label, description),
/// <c>theme.css</c>, and any of <c>layout</c>, <c>home</c>, <c>post</c>, <c>term</c>, <c>not-found</c>; a page a theme
/// doesn't have comes from Paper, the default. <c>_shared/</c> holds what the server renders the same in every theme
/// (the theme bar, the themes page).
///
/// The sandbox: a template sees only the model the server builds (plain dictionaries and lists, never a .NET object, so
/// no member of anything leaks), output is HTML-escaped unless a template marks a value raw (only the sanitized post
/// body and the server's own snippets are), and every render is bounded in steps, recursion, output size and collection
/// size. A theme that fails to render falls back to Paper, and Paper to a bare page, logged either way, so a broken
/// theme never takes the blog down.
/// </summary>
public sealed class Theme
{
    public const string Default = "paper";
    public static readonly string[] Pages = ["layout", "home", "post", "term", "author", "not-found"];

    private static readonly FluidParser Parser = new();

    private readonly TemplateOptions _options = new TemplateOptionsBuilder()
        .WithMaxSteps(100_000)
        .WithMaxRecursion(20)
        .WithMaxOutputSize(4 * 1024 * 1024)
        .WithMaxCollectionSize(10_000)
        .Build();

    private readonly Dictionary<(string Theme, string Page), IFluidTemplate> _templates = [];
    private readonly Dictionary<string, string> _css = new(StringComparer.Ordinal);
    private readonly ILogger<Theme> _log;

    /// <summary>The built-in themes, Paper first, then by label.</summary>
    public IReadOnlyList<ThemeInfo> All { get; }

    public Theme(ILogger<Theme> log)
    {
        _log = log;
        var files = typeof(Theme).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("themes/", StringComparison.Ordinal))
            .ToDictionary(n => n["themes/".Length..].Replace('\\', '/'), n => n);
        var infos = new List<ThemeInfo>();
        foreach (var folder in files.Keys.Select(f => f.Split('/')[0]).Distinct().Where(f => !f.StartsWith('_')))
        {
            var meta = JsonSerializer.Deserialize<JsonElement>(Read(files[$"{folder}/theme.json"]));
            infos.Add(new ThemeInfo(folder, meta.GetProperty("label").GetString()!, meta.GetProperty("description").GetString()!));
            _css[folder] = Read(files[$"{folder}/theme.css"]);
            foreach (var page in Pages)
                if (files.TryGetValue($"{folder}/{page}.liquid", out var resource))
                    _templates[(folder, page)] = Parse($"{folder}/{page}", Read(resource));
        }
        foreach (var shared in new[] { "picker", "themes", "head", "subscribe", "subscribe-form" })
            _templates[("_shared", shared)] = Parse($"_shared/{shared}", Read(files[$"_shared/{shared}.liquid"]));
        PickerCss = Read(files["_shared/picker.css"]);
        SubscribeCss = Read(files["_shared/subscribe.css"]);
        foreach (var page in Pages)
            if (!_templates.ContainsKey((Default, page))) throw new InvalidOperationException($"Paper, the default theme, has no {page}.liquid.");
        All = [.. infos.OrderBy(t => t.Name == Default ? 0 : 1).ThenBy(t => t.Label, StringComparer.Ordinal)];
    }

    public bool Exists(string? name) => name is not null && _css.ContainsKey(name);

    public ThemeInfo Info(string name) => All.First(t => t.Name == (Exists(name) ? name : Default));

    public string Css(string name) => _css[Exists(name) ? name : Default];

    /// <summary>The theme bar's own styles, the same over every theme.</summary>
    public string PickerCss { get; }

    /// <summary>The subscribe form's and pages' styles, the same in every theme (in the theme's colours).</summary>
    public string SubscribeCss { get; }

    /// <summary>
    /// Renders <paramref name="page"/> into the layout of <paramref name="theme"/>. The model's "content" is set here.
    /// A failure in that theme is retried in Paper; a failure in Paper gives the bare page.
    /// </summary>
    public async Task<string> RenderAsync(string theme, string page, Dictionary<string, object?> model)
    {
        if (!Exists(theme)) theme = Default;
        try
        {
            return await RenderInAsync(theme, page, model);
        }
        catch (Exception e) when (e is not OperationCanceledException && theme != Default)
        {
            _log.LogError(e, "The {Theme} theme failed to render {Page}; serving it in Paper.", theme, page);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "Paper failed to render {Page}; serving the bare fallback.", page);
            return Fallback(model);
        }
        try
        {
            // Paper all the way: its templates with its own styles, not the failed theme's CSS on Paper's markup.
            if (model.GetValueOrDefault("theme") is Dictionary<string, object?> shown)
            {
                shown["name"] = Default;
                shown["label"] = Info(Default).Label;
                shown["css_url"] = $"/assets/themes/{Default}.css";
            }
            return await RenderInAsync(Default, page, model);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "Paper failed to render {Page}; serving the bare fallback.", page);
            return Fallback(model);
        }
    }

    /// <summary>One of the shared snippets (<c>picker</c>, <c>themes</c>), rendered with the same sandbox.</summary>
    public Task<string> RenderSharedAsync(string name, Dictionary<string, object?> model) => RenderOneAsync(_templates[("_shared", name)], model);

    private async Task<string> RenderInAsync(string theme, string page, Dictionary<string, object?> model)
    {
        // The page's own HTML; the layout prints it with | raw, like the sanitized post body.
        model["content"] = page is "themes" or "subscribe"
            ? await RenderSharedAsync(page, model)
            : await RenderOneAsync(Template(theme, page), model);
        return await RenderOneAsync(Template(theme, "layout"), model);
    }

    private IFluidTemplate Template(string theme, string page) =>
        _templates.TryGetValue((theme, page), out var t) ? t : _templates[(Default, page)];

    private async Task<string> RenderOneAsync(IFluidTemplate template, Dictionary<string, object?> model)
    {
        var context = new TemplateContext(_options);
        foreach (var (key, value) in model) context.SetValue(key, value ?? NilValue.Instance);
        return await template.RenderAsync(context, HtmlEncoder.Default);
    }

    /// <summary>A bare page that needs no template: the blog's name, and a way back.</summary>
    private static string Fallback(Dictionary<string, object?> model)
    {
        var blog = model.GetValueOrDefault("blog") as Dictionary<string, object?>;
        var title = HtmlEncoder.Default.Encode(blog?.GetValueOrDefault("title") as string ?? "Blog");
        var url = HtmlEncoder.Default.Encode(blog?.GetValueOrDefault("url") as string ?? "/");
        return $"""<!doctype html><html><head><meta charset="utf-8"><title>{title}</title></head><body><p><a href="{url}">{title}</a></p><p>This page can't be shown right now.</p></body></html>""";
    }

    private static IFluidTemplate Parse(string name, string source) =>
        Parser.TryParse(source, out var template, out var error)
            ? template
            : throw new InvalidOperationException($"The built-in theme file {name}.liquid doesn't parse: {error}");

    private static string Read(string resource)
    {
        using var stream = typeof(Theme).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"{resource} isn't embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
