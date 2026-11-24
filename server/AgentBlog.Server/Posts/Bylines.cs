using AgentBlog.Database;

namespace AgentBlog.Posts;

/// <summary>
/// One name on a post: who, what kind (<c>person</c> or <c>agent</c>), what they did, and the person behind it (the
/// person themselves, or whose agent it is), with that person's author page slug when they have one. <see cref="Via"/>
/// is "mcp" when what they did came through an agent, else null (docs/specs/bylines.md): readers see the person, with
/// the channel; the agent's own name stays in structured data and the app's History, never on the public page.
/// </summary>
public sealed record Credit(string Name, string Kind, string Role, string Actor, Guid? Person = null, string? Slug = null, string? OwnName = null,
    string? PersonName = null, string? ProfileUrl = null, string? Via = null)
{
    /// <summary>The name alone: an agent's own name without its person's ("Claude", where <see cref="Name"/> is "Anna's Claude").</summary>
    public string Short => OwnName ?? Name;
}

/// <summary>A piece of a byline sentence: words, or a name that links to its person's author page (<see cref="Slug"/>).</summary>
public sealed record BylinePart(string Text, string? Slug);

/// <summary>
/// A post's byline: the sentence, in parts for linking each name, the people readers see (<see cref="Credits"/>), and
/// the agents behind them (<see cref="Agents"/>, for structured data only — never the visible author).
/// </summary>
public sealed record Byline(string Text, IReadOnlyList<Credit> Credits, IReadOnlyList<BylinePart> Parts, IReadOnlyList<Credit> Agents)
{
    public IEnumerable<Credit> People => Credits.Where(c => c.Kind == "person").DistinctBy(c => c.Actor);
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
            FROM event WHERE blog_id = @blogId AND target_type = 'post' AND kind IN ('post_published', 'post_imported') AND target_id = ANY (@ids)
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

        // Readers see the PERSON, with the channel (docs/specs/bylines.md): an agent's words are its person's, through
        // MCP. Each writer maps to their person; the agent's own name stays in structured data and History, never here.
        string PersonActor(string a) => n.PersonOf(a) is { } pid ? Actors.Account(pid) : a;
        var writerPeople = writers.Select(PersonActor).Distinct().ToList();
        var mcpWrote = writers.Any(a => n.KindOf(a) == "agent");
        var directWrote = writers.Any(a => n.KindOf(a) == "person");
        var channel = mcpWrote && directWrote ? " · partly through MCP" : mcpWrote ? " · through MCP" : "";

        // One credit per writer person, via "mcp" when any of their words came through an agent.
        var credits = writerPeople.Select(p => n.Credit(p, "wrote", "person") with
        {
            Via = writers.Any(a => PersonActor(a) == p && n.KindOf(a) == "agent") ? "mcp" : null,
        }).ToList();
        // The agents behind the words: structured data only (a SoftwareApplication each), never the visible author.
        var agents = writers.Where(a => n.KindOf(a) == "agent").Select(a => AgentCredit(a, "wrote", n)).ToList();

        var s = new Sentence(n);

        if (published is null)
        {
            if (writers.Count == 0) s.Say("Not published yet");
            else
            {
                s.Say("By ");
                s.List(writerPeople);
                s.Say(channel);
                s.Say(" · not published yet");
            }
            return new Byline(s.Text, credits, s.Parts, agents);
        }

        // Who let it out: an account (directly, by approving, or by its schedule), else the agent that published.
        var by = published.Actor == Actors.System ? published.ScheduledBy ?? Actors.System : published.Actor;
        var personLetOut = by.StartsWith("account:", StringComparison.Ordinal);
        var changedSince = revisions
            .Where(r => r.CreatedAt > published.At && Wrote(r) && n.KindOf(r.Actor) == "agent"
                        && !(r.Extra?.Contains("\"approved_by\"", StringComparison.Ordinal) ?? false))
            .Select(r => r.Actor).Distinct().ToList();

        if (personLetOut)
        {
            // A person let it out. When they aren't one of the writers, say they approved it; otherwise it's their own.
            var approver = PersonActor(by);
            var distinctApprover = !writerPeople.Contains(approver);
            s.Say(distinctApprover ? "Written by " : "By ");
            s.List(writerPeople.Count > 0 ? writerPeople : [approver]);
            s.Say(channel);
            if (distinctApprover)
            {
                s.Say(" · approved by ");
                s.Name(approver);
                credits.Add(n.Credit(approver, "approved", "person"));
            }
        }
        else
        {
            // An agent published on its own: its person, published through MCP.
            s.Say(writerPeople.Count > 1 ? "Written by " : "By ");
            s.List(writerPeople);
            if (mcpWrote && directWrote) s.Say(" · partly through MCP");
            s.Say(" · published through MCP");
            credits.Add(n.Credit(PersonActor(by), "published", "person") with { Via = "mcp" });
            agents.Add(AgentCredit(by, "published", n));
        }
        if (changedSince.Count > 0)
        {
            s.Say(" · changed since through MCP");
            foreach (var a in changedSince)
            {
                credits.Add(n.Credit(PersonActor(a), "changed", "person") with { Via = "mcp" });
                agents.Add(AgentCredit(a, "changed", n));
            }
        }
        return new Byline(s.Text, credits, s.Parts, [.. agents.DistinctBy(c => c.Actor)]);
    }

    /// <summary>An agent as structured data sees it: its own name on posts, a SoftwareApplication, never the visible author.</summary>
    private static Credit AgentCredit(string actor, string role, Names n) =>
        new(n.OwnNameOf(actor) ?? n.NameOf(actor), "agent", role, actor, n.PersonOf(actor), null, n.OwnNameOf(actor), null, null, "mcp");

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
            ["name"] = c.Name, ["kind"] = c.Kind, ["role"] = c.Role, ["via"] = c.Via, ["url"] = AuthorUrl(blogUrl, c.Slug), ["profile_url"] = c.ProfileUrl,
        }).ToList(),
        // The people who wrote it, one each: what a theme's author card shows (Midnight's creator card). Their author
        // page, and their own link when they set one. The agents are not named here — readers see the person, with the
        // channel in the byline; the agent's name stays in structured data and the app's History.
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
        credits = b.Credits.Select(c => new { name = c.Name, kind = c.Kind, role = c.Role, via = c.Via }),
    };
}
