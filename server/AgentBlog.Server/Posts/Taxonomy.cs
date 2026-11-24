using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBlog.Auth;
using AgentBlog.Connections;
using AgentBlog.Database;

namespace AgentBlog.Posts;

public sealed record TermView(Guid Id, string Kind, string Slug, string Name, string Description, long LivePosts, long Posts);

/// <summary>A term as a post carries it.</summary>
public sealed record TermRef(Guid Id, string Kind, string Slug, string Name);

/// <summary>A name an agent used that didn't exist, at a level that can't create terms: the owner decides.</summary>
public sealed record TermSuggestion(string Kind, string Name);

/// <summary>
/// A category the owner marked as a section of the blog (docs/specs/taxonomy.md § Sections): its own title, a Markdown
/// intro, an SEO title and meta description, a place in the nav, and whether its posts stay on the home page.
/// </summary>
public sealed record Section(string Title, string Intro, string SeoTitle, string MetaDescription, int NavOrder, bool ShowOnHome)
{
    /// <summary>
    /// A section mark as it stands, tolerantly: anything but an object gives the defaults, and a nav_order that isn't a
    /// whole number (a string, a fraction) gives 0. A malformed mark — a tampered restore, a row from before the
    /// database's object check — never takes a page down with it; the owner can still edit and unmark it.
    /// </summary>
    public static Section Parse(JsonElement e) => e.ValueKind == JsonValueKind.Object ? new(
        Str(e, "title"), Str(e, "intro"), Str(e, "seo_title"), Str(e, "meta_description"),
        e.TryGetProperty("nav_order", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var order) ? order : 0,
        !e.TryGetProperty("show_on_home", out var h) || h.ValueKind != JsonValueKind.False)
        : new("", "", "", "", 0, true);

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public Dictionary<string, object?> ToJson() => new()
    {
        ["title"] = Title, ["intro"] = Intro, ["seo_title"] = SeoTitle, ["meta_description"] = MetaDescription,
        ["nav_order"] = NavOrder, ["show_on_home"] = ShowOnHome,
    };
}

/// <summary>What an edit to a category changes; a field left null stays as it is (docs/specs/taxonomy.md § Sections).</summary>
public sealed record CategoryEdit(string? Name, string? Description, bool? IsSection,
    string? Title, string? Intro, string? SeoTitle, string? MetaDescription, int? NavOrder, bool? ShowOnHome)
{
    public bool IsEmpty => Name is null && Description is null && IsSection is null && Title is null && Intro is null
        && SeoTitle is null && MetaDescription is null && NavOrder is null && ShowOnHome is null;
}

/// <summary>
/// A term an import brings: its name, the source's own address segment when it has one (a category's slug), and a
/// description note when the source carries something this flat term table can't hold as structure — a category's
/// parent, kept as its name in the note (docs/specs/import.md § Category parents).
/// </summary>
public sealed record TermSeed(string Name, string? Slug = null, string? Description = null);

/// <summary>What a term write came to.</summary>
public sealed record TermOutcome(TermView? Term = null, string? Code = null, string? Message = null)
{
    public bool Ok => Code is null;
}

/// <summary>
/// Categories and tags (docs/specs/taxonomy.md). Using an existing term on a post is part of writing it (Propose);
/// creating one is a Publish action (decision 0001's table): below Publish, a new name travels with the revision as a
/// suggestion the owner accepts or dismisses. Editing, merging and deleting terms are Full and come with M2.
/// </summary>
public sealed partial class Taxonomy(Db db, TimeProvider clock)
{
    public const string Category = "category";
    public const string Tag = "tag";
    public const int MaxCategories = 5;
    public const int MaxTags = 20;
    public const int MaxName = 100;
    public const int MaxDescription = 1000;
    public const int MaxSectionTitle = 200;
    public const int MaxIntro = 20_000;
    public const int MaxSeoTitle = 200;
    public const int MaxMetaDescription = 300;

    public static bool IsKind(string? kind) => kind is Category or Tag;

    public Task<IReadOnlyList<TermView>> ListAsync(Guid blogId, string kind, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<TermView>>(async tx => [.. await tx.Query<TermView>("""
            SELECT t.id, t.kind, t.slug, t.name, t.description,
                   count(p.id) FILTER (WHERE p.status = 'live') AS live_posts,
                   count(p.id) FILTER (WHERE p.status <> 'trash') AS posts
            FROM term t
            LEFT JOIN post_revision r ON r.blog_id = t.blog_id AND r.term_ids @> ARRAY[t.id]
            LEFT JOIN post p ON p.current_revision_id = r.id
            WHERE t.blog_id = @blogId AND t.kind = @kind
            GROUP BY t.id
            ORDER BY lower(t.name)
            """, new { blogId, kind })], ct);

    /// <summary>A new term, by name. Publish and up, or the owner; the same name (by its slug) twice is the same term.</summary>
    public async Task<TermOutcome> CreateAsync(Actor actor, string kind, string name, string? description, CancellationToken ct)
    {
        if (!IsKind(kind)) return new(Code: "invalid_argument", Message: "kind is category or tag.");
        if (actor.IsPerson && !actor.Edits)
            return new(Code: "forbidden", Message: $"An editor or the owner creates a {kind}; use it on a draft and it's suggested to them.");
        if (!actor.IsPerson && Levels.Rank(actor.Level!) < Levels.Rank(Levels.Publish))
            return new(Code: "needs_publish", Message:
                $"Creating a {kind} needs Publish; your connection is {actor.Level}. Use it on a draft instead: names that don't exist yet are suggested to the owner.");
        if (CleanName(name) is not { } clean) return new(Code: "invalid_argument", Message: $"A name is 1–{MaxName} characters with at least one letter or digit from A–Z or 0–9.");
        if ((description ?? "").Length > MaxDescription) return new(Code: "invalid_argument", Message: $"description is at most {MaxDescription} characters.");
        var id = await db.WriteAsync(tx => EnsureAsync(tx, actor, kind, clean, description ?? "", clock.GetUtcNow()), ct);
        return new(Term: (await ListAsync(actor.BlogId, kind, ct)).First(t => t.Id == id));
    }

    /// <summary>
    /// The terms a post will carry for one kind, from names: existing ones by slug; new ones created when the actor may
    /// (<paramref name="mayCreate"/>), otherwise returned as suggestions. Runs in the post's own transaction.
    /// </summary>
    internal static async Task<(List<Guid> Ids, List<TermSuggestion> Suggested, string? Error)> ResolveAsync(
        Tx tx, Actor actor, string kind, IReadOnlyList<string> names, bool mayCreate, DateTimeOffset now)
    {
        var max = kind == Category ? MaxCategories : MaxTags;
        var cleaned = new List<string>();
        foreach (var n in names)
        {
            if (CleanName(n) is not { } c) return ([], [], $"Each {kind} name is 1–{MaxName} characters with at least one letter or digit from A–Z or 0–9 (\"{n}\" has none).");
            if (!cleaned.Any(x => Slug(x) == Slug(c))) cleaned.Add(c);
        }
        if (cleaned.Count > max) return ([], [], $"A post has at most {max} {(kind == Category ? "categories" : "tags")}.");

        var ids = new List<Guid>();
        var suggested = new List<TermSuggestion>();
        foreach (var name in cleaned)
        {
            var existing = await tx.One<Guid?>("SELECT id FROM term WHERE blog_id = @BlogId AND kind = @kind AND slug = @slug",
                new { actor.BlogId, kind, slug = Slug(name) });
            if (existing is { } id) ids.Add(id);
            else if (mayCreate) ids.Add(await EnsureAsync(tx, actor, kind, name, "", now));
            else suggested.Add(new TermSuggestion(kind, name));
        }
        return (ids, suggested, null);
    }

    /// <summary>
    /// The term with this name's slug, created (with an event) if it isn't there yet. <paramref name="slug"/>, when
    /// given (an import keeping the source's own address), is the address instead of the name's; it must be one the
    /// term table takes (<see cref="IsSlug"/>).
    /// </summary>
    internal static async Task<Guid> EnsureAsync(Tx tx, Actor actor, string kind, string name, string description, DateTimeOffset now, string? slug = null)
    {
        slug ??= Slug(name)!;
        var id = Guid.CreateVersion7();
        var created = await tx.One<Guid?>("""
            INSERT INTO term (id, blog_id, kind, slug, name, description, created_by, created_at)
            VALUES (@id, @BlogId, @kind, @slug, @name, @description, @Value, @now)
            ON CONFLICT (blog_id, kind, slug) DO NOTHING
            RETURNING id
            """, new { id, actor.BlogId, kind, slug, name, description, actor.Value, now });
        if (created is null)
            return await tx.One<Guid>("SELECT id FROM term WHERE blog_id = @BlogId AND kind = @kind AND slug = @slug", new { actor.BlogId, kind, slug });
        await tx.Event(new EventRow(actor.BlogId, "term", id, "term_created", actor.Value, new { kind, name, slug }), now);
        return id;
    }

    /// <summary>The kinds of these term ids (to replace one kind's set and keep the other's).</summary>
    internal static async Task<Dictionary<Guid, string>> KindsAsync(Tx tx, Guid blogId, IReadOnlyCollection<Guid> ids) =>
        ids.Count == 0 ? [] : (await tx.Query<(Guid Id, string Kind)>("SELECT id, kind FROM term WHERE blog_id = @blogId AND id = ANY(@ids)",
            new { blogId, ids = ids.ToArray() })).ToDictionary(t => t.Id, t => t.Kind);

    /// <summary>
    /// A name with its spaces tidied, or null. It needs at least one letter or digit that survives into an address
    /// (accents are dropped: "Čeština" is <c>cestina</c>); a name of only other scripts has no address yet and is refused
    /// with that reason, rather than collapsing into one shared fallback slug.
    /// </summary>
    internal static string? CleanName(string? name)
    {
        var n = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return n.Length is >= 1 and <= MaxName && Slug(n) is not null ? n : null;
    }

    /// <summary>The term's address segment, or null when the name has nothing that survives into one.</summary>
    internal static string? Slug(string name)
    {
        var slug = PostService.Slugify(name);
        return slug == "post" && !name.Contains("post", StringComparison.OrdinalIgnoreCase) ? null : slug;
    }

    /// <summary>Whether this is an address segment the term table takes (the slug domain's own grammar).</summary>
    internal static bool IsSlug(string? s) => s is { Length: >= 1 and <= 100 } && SlugGrammar().IsMatch(s);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,98}[a-z0-9])?$")]
    private static partial Regex SlugGrammar();

    // --------------------------------------------------------------------------------------------- sections

    /// <summary>A category as its owner edits it, with its section mark (null when it isn't a section).</summary>
    public sealed record CategoryRow(Guid Id, string Slug, string Name, string Description, string? SectionJson)
    {
        public Section? Section => SectionJson is null ? null : Section.Parse(JsonDocument.Parse(SectionJson).RootElement);
    }

    /// <summary>The category with this slug (or id), or null. Only categories: a tag is never a section.</summary>
    public Task<CategoryRow?> GetCategoryAsync(Guid blogId, string category, CancellationToken ct) => db.ReadAsync(tx => tx.One<CategoryRow>("""
        SELECT id, slug, name, description, section::text AS section_json FROM term
        WHERE blog_id = @blogId AND kind = 'category' AND (slug = @category OR id::text = @category)
        """, new { blogId, category }), ct);

    /// <summary>
    /// Every category marked as a section, in nav order then by name (the site nav, the home page, the sitemap). A
    /// nav_order that isn't a number orders as 0: a malformed mark never breaks the query.
    /// </summary>
    public Task<IReadOnlyList<CategoryRow>> SectionsAsync(Guid blogId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<CategoryRow>>(async tx => [.. await tx.Query<CategoryRow>("""
            SELECT id, slug, name, description, section::text AS section_json FROM term
            WHERE blog_id = @blogId AND kind = 'category' AND section IS NOT NULL
            ORDER BY coalesce(CASE WHEN jsonb_typeof(section->'nav_order') = 'number' THEN (section->>'nav_order')::numeric END, 0), lower(name)
            """, new { blogId })], ct);

    /// <summary>
    /// The owner (or an agent at Full) edits a category: its name, description, and its section mark and fields. A field
    /// left null stays. Below Full an agent's edit becomes a request (<see cref="Blogs.BlogRequests"/>); the core gates
    /// the actor here too, so no face skips the rule.
    /// </summary>
    public async Task<TermOutcome> EditCategoryAsync(Actor actor, string category, CategoryEdit edit, CancellationToken ct)
    {
        if (actor.IsPerson && !actor.IsOwner)
            return new(Code: "forbidden", Message: "Only the owner edits a category.");
        if (!actor.IsPerson && Levels.Rank(actor.Level!) < Levels.Rank(Levels.Full))
            return new(Code: "needs_full", Message: $"Editing a category needs Full; your connection is {actor.Level}.");
        if (ValidateEdit(edit) is { } bad) return new(Code: "invalid_argument", Message: bad);
        var id = await db.WriteAsync(tx => ApplyCategoryEditAsync(tx, actor.BlogId, category, edit, actor.Value, clock.GetUtcNow()), ct);
        if (id is null) return new(Code: "not_found", Message: "No such category on this blog.");
        return new(Term: (await ListAsync(actor.BlogId, Category, ct)).First(t => t.Id == id));
    }

    /// <summary>
    /// Applies a category edit in the caller's transaction, naming <paramref name="actor"/> in the event: the one write
    /// the owner's API, a Full agent and an approved request all share. Returns the term's id, or null when there is no
    /// such category. A no-op (nothing sent, or nothing changed) writes no event.
    /// </summary>
    internal static async Task<Guid?> ApplyCategoryEditAsync(Tx tx, Guid blogId, string category, CategoryEdit edit, string actor, DateTimeOffset now)
    {
        var row = await tx.One<CategoryRow>("""
            SELECT id, slug, name, description, section::text AS section_json FROM term
            WHERE blog_id = @blogId AND kind = 'category' AND (slug = @category OR id::text = @category) FOR UPDATE
            """, new { blogId, category });
        if (row is null) return null;
        if (ValidateEdit(edit) is { } bad) throw new ArgumentException(bad);

        var name = edit.Name is null ? row.Name : CleanName(edit.Name)!;
        var description = edit.Description is null ? row.Description : edit.Description.Trim();
        var section = NewSection(row.Section, edit, name);
        var sectionJson = section is null ? null : JsonSerializer.Serialize(section.ToJson(), Json.Options);

        var changed = new Dictionary<string, object>();
        if (name != row.Name) changed["name"] = new { from = row.Name, to = name };
        if (description != row.Description) changed["description"] = new { from = row.Description, to = description };
        var beforeSection = row.Section is { } before ? JsonSerializer.Serialize(before.ToJson(), Json.Options) : null;
        if (sectionJson != beforeSection) changed["section"] = new { from = row.Section, to = section };
        if (changed.Count > 0)
        {
            await tx.Exec("UPDATE term SET name = @name, description = @description, section = @section::jsonb WHERE blog_id = @blogId AND id = @id",
                new { name, description, section = sectionJson, blogId, id = row.Id });
            await tx.Event(new EventRow(blogId, "term", row.Id, "term_changed", actor, changed), now);
        }
        return row.Id;
    }

    /// <summary>The section after an edit: null when unmarked, the current one with the sent fields replaced otherwise.</summary>
    private static Section? NewSection(Section? current, CategoryEdit edit, string name)
    {
        if (edit.IsSection is false) return null;
        if (edit.IsSection is null && current is null) return null; // never marked, and not marking now
        var basis = current ?? new Section(name, "", "", "", 0, true);
        return basis with
        {
            Title = edit.Title is null ? (current is null ? name : basis.Title) : edit.Title.Trim(),
            Intro = edit.Intro is null ? basis.Intro : edit.Intro,
            SeoTitle = edit.SeoTitle is null ? basis.SeoTitle : edit.SeoTitle.Trim(),
            MetaDescription = edit.MetaDescription is null ? basis.MetaDescription : edit.MetaDescription.Trim(),
            NavOrder = edit.NavOrder ?? basis.NavOrder,
            ShowOnHome = edit.ShowOnHome ?? basis.ShowOnHome,
        };
    }

    /// <summary>What's wrong with an edit, or null when it's well-formed.</summary>
    public static string? ValidateEdit(CategoryEdit edit)
    {
        if (edit.Name is not null && CleanName(edit.Name) is null)
            return $"A name is 1–{MaxName} characters with at least one letter or digit from A–Z or 0–9.";
        if (edit.Description is { Length: > MaxDescription }) return $"description is at most {MaxDescription} characters.";
        if (edit.Title is { Length: > MaxSectionTitle }) return $"A section title is at most {MaxSectionTitle} characters.";
        if (edit.Intro is { Length: > MaxIntro }) return $"A section intro is at most {MaxIntro} characters.";
        if (edit.SeoTitle is { Length: > MaxSeoTitle }) return $"An SEO title is at most {MaxSeoTitle} characters.";
        if (edit.MetaDescription is { Length: > MaxMetaDescription }) return $"A meta description is at most {MaxMetaDescription} characters.";
        return null;
    }

    public static List<TermRef> Parse(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<TermRef>>(json, Json.Options) ?? [];

    public static List<TermSuggestion> ParseSuggested(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<TermSuggestion>>(json, Json.Options) ?? [];

    public static string Serialize(IEnumerable<TermSuggestion> s) => JsonSerializer.Serialize(s, Json.Options);
}
