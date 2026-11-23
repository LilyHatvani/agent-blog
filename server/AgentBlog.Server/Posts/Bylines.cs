using AgentBlog.Database;

namespace AgentBlog.Posts;

/// <summary>
/// One name on a post: who, what kind (<c>person</c> or <c>agent</c>), what they did, and the person behind it (the
/// person themselves, or whose agent it is), with that person's author page slug when they have one.
/// </summary>
public sealed record Credit(string Name, string Kind, string Role, string Actor, Guid? Person = null, string? Slug = null, string? OwnName = null,
    string? PersonName = null, string? ProfileUrl = null)
{
    /// <summary>The name alone: an agent's own name without its person's ("Claude", where <see cref="Name"/> is "Anna's Claude").</summary>
    public string Short => OwnName ?? Name;
}

/// <summary>A piece of a byline sentence: words, or a name that links to its person's author page (<see cref="Slug"/>).</summary>
public sealed record BylinePart(string Text, string? Slug);

/// <summary>A post's byline: the sentence, in parts for linking each name, and each name behind it.</summary>
public sealed record Byline(string Text, IReadOnlyList<Credit> Credits, IReadOnlyList<BylinePart> Parts)
{
    public IEnumerable<Credit> People => Credits.Where(c => c.Kind == "person").DistinctBy(c => c.Actor);
    public IEnumerable<Credit> Agents => Credits.Where(c => c.Kind == "agent").DistinctBy(c => c.Actor);
    public IEnumerable<Credit> Writers => Credits.Where(c => c.Role == "wrote");
}

/// <summary>
/// Honest bylines (docs/specs/bylines.md): who wrote a post and who let it out, derived from its revisions, its first
/// publication and the approvals, never typed in. Fixed templates, no model.
/// </summary>
public sealed class Bylines(Db db)
{
    /// <summary>Whether the blog shows bylines (on unless the owner turned them off).</summary>
    public Task<bool> OnAsync(Guid blogId, CancellationToken ct) => db.ReadAsync(async tx =>
        await tx.One<bool?>("SELECT (settings->>'bylines')::boolean FROM blog WHERE id = @blogId", new { blogId }) ?? true, ct);

    private sealed record RevisionFact(Guid PostId, int Number, string Actor, DateTime CreatedAt, string? Kind, string? Extra);
    private sealed record Published(Guid PostId, string Actor, string? ScheduledBy, DateTime At);

    /// <summary>The byline of each post in <paramref name="postIds"/> (one query per kind, for a feed of many).</summary>
    public Task<Dictionary<Guid, Byline>> ForPostsAsync(Guid blogId, IReadOnlyCollection<Guid> postIds, CancellationToken ct) => db.ReadAsync(async tx =>
    {
        var ids = postIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, Byline>();
        // Each revision with the event that saved it: what kind of save, and its extra (approved_by, a suggestion).
        var revisions = (await tx.Query<RevisionFact>("""
            SELECT r.post_id, r.number, r.actor, r.created_at, e.kind, (e.payload->'extra')::text AS extra
            FROM post_revision r
            LEFT JOIN LATERAL (
                SELECT kind, payload FROM event
                WHERE target_type = 'post' AND target_id = r.post_id AND kind IN ('revision_saved', 'revision_restored')
                  AND (payload->>'revision')::int = r.number
                ORDER BY id LIMIT 1) e ON true
            WHERE r.blog_id = @blogId AND r.post_id = ANY (@ids)
            ORDER BY r.post_id, r.number
            """, new { blogId, ids })).ToList();
        var published = (await tx.Query<Published>("""
            SELECT DISTINCT ON (target_id) target_id AS post_id, actor, payload->>'scheduled_by' AS scheduled_by, at
            FROM event WHERE blog_id = @blogId AND target_type = 'post' AND kind = 'post_published' AND target_id = ANY (@ids)
            ORDER BY target_id, id
            """, new { blogId, ids })).ToDictionary(p => p.PostId);
        // Each agent and whose it is (docs/specs/members.md § Their agents).
        var agents = (await tx.Query<Agent>("""
            SELECT c.id, coalesce(c.byline_name, nullif(split_part(c.label, ' · ', 1), ''), c.label) AS name, c.created_by AS person,
                   coalesce(c.created_by = o.account_id, false) AS owners
            FROM connection c LEFT JOIN blog_member o ON o.blog_id = c.blog_id AND o.role = 'owner'
            WHERE c.blog_id = @blogId
            """, new { blogId })).ToDictionary(c => Actors.Connection(c.Id));
        // People by their account's name; the owner's "Your name on posts" is the owner's alone. Someone removed from the
        // blog is still named, and keeps their author page: the account outlives the membership.
        var accountIds = revisions.Select(r => r.Actor).Concat(published.Values.SelectMany(p => new[] { p.Actor, p.ScheduledBy ?? "" }))
            .Where(a => a.StartsWith("account:", StringComparison.Ordinal)).Select(a => Guid.TryParse(a[8..], out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).Concat(agents.Values.Select(a => a.Person)).Distinct().ToArray();
        var people = (await tx.Query<Person>("""
            SELECT a.id, CASE WHEN m.role = 'owner' THEN coalesce(nullif(b.settings->>'author_name', ''), a.display_name) ELSE a.display_name END AS name,
                   au.slug, au.profile_url
            FROM account a JOIN blog b ON b.id = @blogId
            LEFT JOIN blog_member m ON m.blog_id = b.id AND m.account_id = a.id
            LEFT JOIN author au ON au.blog_id = b.id AND au.account_id = a.id
            WHERE a.id = ANY (@accountIds)
            """, new { blogId, accountIds })).ToDictionary(p => p.Id);

        Guid? PersonOf(string actor) => agents.TryGetValue(actor, out var a) ? a.Person
            : actor.StartsWith("account:", StringComparison.Ordinal) && Guid.TryParse(actor[8..], out var g) ? g : null;
        // An agent goes by its own name on the owner's blog when it is the owner's; anyone else's carries their name too,
        // so two people's "Claude" are never one name.
        string NameOf(string actor) => agents.TryGetValue(actor, out var a)
                ? a.Owners || !people.TryGetValue(a.Person, out var whose) ? a.Name : $"{whose.Name}'s {a.Name}"
            : PersonOf(actor) is { } id && people.TryGetValue(id, out var p) ? p.Name
            : actor.StartsWith("account:", StringComparison.Ordinal) ? "a removed account" : "the blog";
        string KindOf(string actor) => actor.StartsWith("connection:", StringComparison.Ordinal) ? "agent" : "person";
        string? SlugOf(string actor) => PersonOf(actor) is { } id && people.TryGetValue(id, out var p) ? p.Slug : null;
        var names = new Names(NameOf, KindOf, PersonOf, SlugOf, actor => agents.TryGetValue(actor, out var a) ? a.Name : null,
            actor => PersonOf(actor) is { } id && people.TryGetValue(id, out var p) ? p : null);

        var result = new Dictionary<Guid, Byline>();
        foreach (var id in ids)
            result[id] = Build(revisions.Where(r => r.PostId == id).ToList(), published.GetValueOrDefault(id), names);
        return result;
    }, ct);

    public async Task<Byline?> ForPostAsync(Guid blogId, Guid postId, CancellationToken ct) =>
        (await ForPostsAsync(blogId, [postId], ct)).GetValueOrDefault(postId);

    private sealed record Agent(Guid Id, string Name, Guid Person, bool Owners);
    private sealed record Person(Guid Id, string Name, string? Slug, string? ProfileUrl);

    /// <summary>How each actor is named, what kind it is, and whose author page it belongs to.</summary>
    private sealed record Names(Func<string, string> NameOf, Func<string, string> KindOf, Func<string, Guid?> PersonOf, Func<string, string?> SlugOf,
        Func<string, string?> OwnNameOf, Func<string, Person?> PersonRowOf)
    {
        public Credit Credit(string actor, string role, string? kind = null) =>
            new(NameOf(actor), kind ?? KindOf(actor), role, actor, PersonOf(actor), SlugOf(actor), OwnNameOf(actor),
                PersonRowOf(actor)?.Name, PersonRowOf(actor)?.ProfileUrl);
    }

    /// <summary>A byline sentence being written: plain words, and names that link to their person's author page.</summary>
    private sealed class Sentence(Names names)
    {
        public readonly List<BylinePart> Parts = [];

        public void Say(string text)
        {
            if (Parts.Count > 0 && Parts[^1].Slug is null) Parts[^1] = Parts[^1] with { Text = Parts[^1].Text + text };
            else Parts.Add(new BylinePart(text, null));
        }

        public void Name(string actor) => Parts.Add(new BylinePart(names.NameOf(actor), names.SlugOf(actor)));

        /// <summary>"A", "A and B", "A, B and C": one per name, however many actors share it.</summary>
        public void List(IEnumerable<string> actors)
        {
            var unique = actors.GroupBy(names.NameOf).Select(g => g.First()).ToList();
            for (var i = 0; i < unique.Count; i++)
            {
                if (i > 0) Say(i == unique.Count - 1 ? " and " : ", ");
                Name(unique[i]);
            }
        }

        public string Text => string.Concat(Parts.Select(p => p.Text));
    }

    private static Byline Build(List<RevisionFact> revisions, Published? published, Names n)
    {
        // Writers: a revision with new words. A restore copies old words; a tag suggestion decided changes no words.
        static bool Wrote(RevisionFact r) => r.Kind != "revision_restored" && !(r.Extra?.Contains("\"suggestion\"", StringComparison.Ordinal) ?? false);
        var writers = revisions.Where(Wrote).Select(r => r.Actor).Where(a => a != Actors.System).Distinct().ToList();
        var credits = writers.Select(a => n.Credit(a, "wrote")).ToList();
        var s = new Sentence(n);

        if (published is null)
        {
            if (writers.Count == 0) s.Say("Not published yet");
            else
            {
                s.Say(writers.All(a => n.KindOf(a) == "person") ? "By " : "Written by ");
                s.List(writers);
                s.Say(" · not published yet");
            }
            return new Byline(s.Text, credits, s.Parts);
        }

        // Who let it out: an account (directly, by approving, or by its schedule), else the agent that published.
        var by = published.Actor == Actors.System ? published.ScheduledBy ?? Actors.System : published.Actor;
        var personLetOut = by.StartsWith("account:", StringComparison.Ordinal);
        var changedSince = revisions
            .Where(r => r.CreatedAt > published.At && Wrote(r) && n.KindOf(r.Actor) == "agent"
                        && !(r.Extra?.Contains("\"approved_by\"", StringComparison.Ordinal) ?? false))
            .Select(r => r.Actor).Distinct().ToList();

        if (writers.All(a => n.KindOf(a) == "person") && personLetOut)
        {
            s.Say("By ");
            s.List(writers.Count > 0 ? writers : [by]);
        }
        else if (personLetOut)
        {
            s.Say("Written by ");
            s.List(writers);
            s.Say(" · approved by ");
            s.Name(by);
            credits.Add(n.Credit(by, "approved", "person"));
        }
        else
        {
            if (writers.Count == 1 && writers[0] == by) s.Say("Written and published by ");
            else
            {
                s.Say("Written by ");
                s.List(writers);
                s.Say(" · published by ");
            }
            s.Name(by);
            credits.Add(n.Credit(by, "published"));
        }
        if (changedSince.Count > 0)
        {
            s.Say(" · changed since by ");
            s.List(changedSince);
            credits.AddRange(changedSince.Select(a => n.Credit(a, "changed", "agent")));
        }
        return new Byline(s.Text, credits, s.Parts);
    }

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string List(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>
    /// For a theme: the sentence, the sentence in parts (each name with its author page's URL, to link), and each name,
    /// its kind, what it did and its author page.
    /// </summary>
    public static Dictionary<string, object?> Model(Byline b, string blogUrl) => new()
    {
        ["text"] = b.Text,
        ["parts"] = b.Parts.Select(p => new Dictionary<string, object?> { ["text"] = p.Text, ["url"] = AuthorUrl(blogUrl, p.Slug) }).ToList(),
        ["credits"] = b.Credits.Select(c => new Dictionary<string, object?>
        {
            ["name"] = c.Name, ["kind"] = c.Kind, ["role"] = c.Role, ["url"] = AuthorUrl(blogUrl, c.Slug), ["profile_url"] = c.ProfileUrl,
        }).ToList(),
        // The people who wrote it, one each, with the agents that wrote for them: what a theme's author card shows
        // (Midnight's creator card). Their author page, and their own link when they set one.
        ["authors"] = b.Writers.Where(c => c.Person is not null).GroupBy(c => c.Person).Select(g => new Dictionary<string, object?>
        {
            ["name"] = g.First().PersonName ?? g.First().Name,
            ["url"] = AuthorUrl(blogUrl, g.First().Slug),
            ["profile_url"] = g.First().ProfileUrl,
            ["agents"] = g.Where(c => c.Kind == "agent").Select(c => c.Short).Distinct().ToList(),
            ["initial"] = (g.First().PersonName ?? g.First().Name).Trim() is { Length: > 0 } n ? char.ToUpperInvariant(n[0]).ToString() : "?",
        }).ToList(),
    };

    /// <summary>A person's author page (docs/specs/authors.md), or null when there is none to link.</summary>
    public static string? AuthorUrl(string blogUrl, string? slug) => slug is null ? null : $"{blogUrl}author/{slug}";

    public static object Json(Byline b) => new
    {
        text = b.Text,
        credits = b.Credits.Select(c => new { name = c.Name, kind = c.Kind, role = c.Role }),
    };
}
