using System.Text.Json;
using AgentBlog.Auth;
using AgentBlog.Database;

namespace AgentBlog.Demo;

/// <summary>
/// The demo (docs/specs/demo.md): with <c>Blog__Demo__Enabled</c>, <c>/demo</c> opens the owner's app as a guest on a
/// sample blog, so a visitor sees what running a blog with an agent looks like before deploying one. What keeps it
/// harmless:
/// <list type="bullet">
/// <item>Its own blog, hidden: the public site never serves it, and instance-wide questions ("who is the owner?" for
/// setup and recovery) look past it. The real blog and its people are never in reach.</item>
/// <item>Every change is refused, with the reason (<see cref="Refusal"/>): the owner API's writes, and connecting an
/// AI. Signing out still works. Turning the demo off ends its sessions on their next request.</item>
/// <item>One guest account for everyone: a visitor's session is the guest's, so nothing per visitor is kept but a
/// session row, and sign-in rate limits apply to <c>/demo</c>.</item>
/// </list>
/// </summary>
public sealed class DemoBlog(Db db, IConfiguration config, TimeProvider clock)
{
    public static readonly Guid AccountId = Guid.Parse("01940000-de30-7000-8000-000000000001");
    public static readonly Guid BlogId = Guid.Parse("01940000-de30-7000-8000-000000000002");
    public const string Provider = "demo";
    private const string GuestId = "guest";

    public const string Refusal =
        "This is a demo: look around as much as you like, but nothing here can be changed. To write with your own AI, deploy your own blog.";

    public bool Enabled => config.GetValue("Blog:Demo:Enabled", false);

    public static bool IsGuest(Guid accountId) => accountId == AccountId;

    /// <summary>Starts a guest session on the demo blog (making the blog the first time).</summary>
    public async Task StartAsync(HttpContext ctx, Sessions sessions, CancellationToken ct)
    {
        await EnsureAsync(ct);
        await sessions.StartAsync(ctx, AccountId, Provider, GuestId, ct);
    }

    /// <summary>Makes the demo blog once: the guest (its owner), a writer, two agents, posts in each state, requests waiting.</summary>
    public Task EnsureAsync(CancellationToken ct) => db.WriteAsync(async tx =>
    {
        if (await tx.One<bool>("SELECT EXISTS (SELECT 1 FROM blog WHERE id = @BlogId)", new { BlogId })) return 0;
        var now = clock.GetUtcNow();
        DateTimeOffset Ago(double hours) => now - TimeSpan.FromHours(hours);
        var slug = await tx.One<bool>("SELECT EXISTS (SELECT 1 FROM blog WHERE slug = 'demo')") ? "demo-" + Guid.NewGuid().ToString("N")[..8] : "demo";
        var anna = Guid.CreateVersion7();
        var (claude, claudeCode) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        string Account(Guid id) => $"account:{id}";
        string Connection(Guid id) => $"connection:{id}";

        await tx.Exec("""
            INSERT INTO account (id, display_name, created_at) VALUES (@AccountId, 'Sam', @t0), (@anna, 'Anna', @t1);
            INSERT INTO account_identity (provider, provider_account_id, account_id, display_name, created_at)
                VALUES ('demo', 'guest', @AccountId, 'Demo guest', @t0);
            INSERT INTO blog (id, slug, title, description, created_at, hidden_at, settings)
                VALUES (@BlogId, @slug, 'Notes from the Workshop', 'Building things by hand, with an AI at the next bench.', @t0, @t0, @settings::jsonb);
            INSERT INTO blog_member (blog_id, account_id, role, created_at) VALUES (@BlogId, @AccountId, 'owner', @t0), (@BlogId, @anna, 'writer', @t1);
            INSERT INTO connection (id, blog_id, label, level, created_by, created_at, last_used_at, client_id, byline_name) VALUES
                (@claude, @BlogId, 'Claude on my phone', 'propose', @AccountId, @t1, @t9, 'https://claude.ai/oauth/mcp-oauth-client-metadata', 'Claude'),
                (@claudeCode, @BlogId, 'Claude Code', 'publish', @AccountId, @t2, @t7, 'claude-code', 'Claude Code');
            """, new { AccountId, BlogId, anna, slug, claude, claudeCode, t0 = Ago(96), t1 = Ago(72), t2 = Ago(48), t7 = Ago(3), t9 = Ago(0.2),
                settings = JsonSerializer.Serialize(new { theme = "paper", bylines = true }) });

        async Task<(Guid Post, Guid Revision)> PostAsync(string postSlug, string status, string actor, double createdHoursAgo,
            params (string Title, string Body, string Excerpt, string By, double HoursAgo)[] revisions)
        {
            var post = Guid.CreateVersion7();
            var ids = revisions.Select(_ => Guid.CreateVersion7()).ToArray();
            var last = revisions[^1];
            await tx.Exec("""
                INSERT INTO post (id, blog_id, slug, status, created_at, updated_at, published_at)
                VALUES (@post, @BlogId, @postSlug, 'draft', @created, @updated, NULL)
                """, new { post, BlogId, postSlug, created = Ago(createdHoursAgo), updated = Ago(last.HoursAgo) });
            for (var i = 0; i < revisions.Length; i++)
            {
                var r = revisions[i];
                await tx.Exec("""
                    INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, excerpt, actor, created_at)
                    VALUES (@id, @BlogId, @post, @number, @Title, @Body, @Excerpt, @By, @at)
                    """, new { id = ids[i], BlogId, post, number = i + 1, r.Title, r.Body, r.Excerpt, r.By, at = Ago(r.HoursAgo) });
                await tx.Event(new EventRow(BlogId, "post", post, i == 0 ? "post_created" : "revision_saved", r.By,
                    i == 0 ? null : new { revision = i + 1 }), Ago(r.HoursAgo));
            }
            await tx.Exec("""
                UPDATE post SET current_revision_id = @rev, status = @status,
                    published_at = CASE WHEN @status = 'live' THEN @updated END
                WHERE id = @post
                """, new { post, rev = ids[^1], status, updated = Ago(last.HoursAgo) });
            if (status == "live")
                await tx.Event(new EventRow(BlogId, "post", post, "post_published", actor, null), Ago(last.HoursAgo));
            return (post, ids[^1]);
        }

        var sam = Account(AccountId);
        var (why, whyRev) = await PostAsync("why-i-write-with-an-agent", "live", sam, 70,
            ("Why I write with an agent", "I started this blog to think out loud.\n\nThe agent drafts; I decide.",
                "On drafting with an AI and keeping the publish button.", Connection(claude), 70),
            ("Why I write with an agent",
                "I started this blog to think out loud, and an agent turned out to be a good **sparring partner**.\n\n## What it does\n\n- drafts from my notes\n- fixes my typos\n- asks before anything goes live\n\n> The agent drafts; I decide.\n\nEverything it changes is a revision I can put back.",
                "On drafting with an AI and keeping the publish button.", Connection(claude), 46));
        await PostAsync("the-workbench", "live", sam, 40,
            ("The workbench", "A short tour of the bench: the vice, the saw, and the laptop that writes the notes.\n\nSome of these posts start as a voice memo on a walk. Claude turns it into a draft; I cut it in half.",
                "A short tour of the bench.", sam, 26));
        var (week, weekRev) = await PostAsync("notes-from-the-week", "review", sam, 3,
            ("Notes from the week", "Three things I learned this week about building a blog engine in a weekend.\n\n1. Revisions first.\n2. Levels second.\n3. Everything else is CSS.",
                "Revisions, levels, CSS.", Connection(claude), 0.7));
        await PostAsync("half-an-idea", "draft", sam, 0.4,
            ("Half an idea", "What if the trash were a feature?", "", Connection(claudeCode), 0.4));
        await PostAsync("first-dovetail", "review", sam, 20,
            ("My first dovetail", "It took four tries. The fourth one closes.\n\nHere is what changed between the third and the fourth.",
                "Four tries, one joint.", Account(anna), 5));

        // Waiting in the Inbox: an agent's edit to a live post, and its draft asking to go live.
        var editBody = "I started this blog to think out loud, and an agent turned out to be a good **sparring partner**.\n\n## What it does\n\n- drafts from my notes\n- fixes my typos (it found four in this post)\n- asks before anything goes live\n\n> The agent drafts; I decide.\n\nEverything it changes is a revision I can put back.";
        var (editId, publishId) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await tx.Exec("""
            INSERT INTO approval_request (id, blog_id, connection_id, action, post_id, pinned_revision_id, payload, state, created_at) VALUES
                (@editId, @BlogId, @claude, 'edit', @why, @whyRev, @edit::jsonb, 'waiting', @t12m),
                (@publishId, @BlogId, @claude, 'publish', @week, @weekRev, '{}'::jsonb, 'waiting', @t35m)
            """, new { editId, publishId, BlogId, claude, why, whyRev, week, weekRev, t12m = Ago(0.2), t35m = Ago(0.6),
                edit = JsonSerializer.Serialize(new { title = "Why I write with an agent", excerpt = "On drafting with an AI and keeping the publish button.", body_md = editBody }) });
        await tx.Event(new EventRow(BlogId, "approval_request", publishId, "approval_requested", Connection(claude), new { action = "publish" }), Ago(0.6));
        await tx.Event(new EventRow(BlogId, "approval_request", editId, "approval_requested", Connection(claude), new { action = "edit" }), Ago(0.2));
        await tx.Event(new EventRow(BlogId, "connection", claudeCode, "connection_level_changed", sam, new { from = "propose", to = "publish" }), Ago(30));
        return 0;
    }, ct);
}

public static class DemoEndpoints
{
    /// <summary><c>GET /demo</c>: a guest session on the demo blog, then the app. Not found when the demo is off.</summary>
    public static void MapDemo(this WebApplication app) =>
        app.MapGet("/demo", async (HttpContext ctx, DemoBlog demo, Sessions sessions) =>
        {
            if (!demo.Enabled) return Results.NotFound();
            await demo.StartAsync(ctx, sessions, ctx.RequestAborted);
            return Results.Redirect("/admin");
        }).WithMetadata(SignIn.AuthRateLimited.Instance);

}
