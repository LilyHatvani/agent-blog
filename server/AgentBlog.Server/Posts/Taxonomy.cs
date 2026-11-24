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

/// <summary>A term an import brings: its name, and the source's own address segment when it has one (a category's slug).</summary>
public sealed record TermSeed(string Name, string? Slug = null);

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

    public static List<TermRef> Parse(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<TermRef>>(json, Json.Options) ?? [];

    public static List<TermSuggestion> ParseSuggested(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<TermSuggestion>>(json, Json.Options) ?? [];

    public static string Serialize(IEnumerable<TermSuggestion> s) => JsonSerializer.Serialize(s, Json.Options);
}
