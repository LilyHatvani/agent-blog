using System.Text.Json;
using AgentBlog.Connections;
using AgentBlog.Database;
using AgentBlog.Media;
using AgentBlog.Posts;

namespace AgentBlog.Blogs;

/// <summary>A request beyond posts, as the owner and the agent see it.</summary>
public sealed record BlogRequestView(Guid Id, Guid BlogId, Guid ConnectionId, string ConnectionLabel, string Action, Guid? MediaId,
    string? MediaFilename, string? MediaPath, string Payload, string Pinned, string State, DateTime CreatedAt, DateTime? DecidedAt,
    string? DecisionNote, string? StaleWhy);

/// <summary>
/// Approval requests beyond posts (docs/specs/requests.md § Beyond posts, M3): trashing a picture and changing the
/// blog's title or description. At Full an agent does them at once; below, they become a request only the owner
/// decides, pinned to what the agent saw. Approving applies exactly that, unless what it was about changed since (the
/// picture isn't in use, the settings moved on): then it turns stale and nothing applies, the same rule as posts.
/// </summary>
public sealed class BlogRequests(Db db, MediaService media, TimeProvider clock, Taxonomy terms)
{
    public sealed record Outcome(bool Done, BlogRequestView? Request, string? Code, string? Message)
    {
        public static Outcome Fail(string code, string message) => new(false, null, code, message);
    }

    private const string Select = """
        SELECT q.id, q.blog_id, q.connection_id, c.label AS connection_label, q.action, q.media_id, m.filename AS media_filename,
               CASE WHEN m.id IS NULL THEN NULL ELSE '/media/' || m.id::text || '/' || m.filename END AS media_path,
               q.payload::text AS payload, q.pinned::text AS pinned, q.state, q.created_at, q.decided_at, q.decision_note,
               CASE WHEN q.action = 'trash_media' AND m.status <> 'ready' THEN 'The picture isn''t in use any more.'
                    WHEN q.action = 'edit_settings' AND (b.title <> q.pinned->>'title' OR b.description <> q.pinned->>'description')
                         THEN 'The blog''s title or description changed since the agent asked.'
                    WHEN q.action = 'edit_category' AND NOT EXISTS (
                         SELECT 1 FROM term t WHERE t.blog_id = q.blog_id AND t.kind = 'category' AND t.slug = q.pinned->>'slug'
                           AND t.name = q.pinned->>'name' AND t.description = q.pinned->>'description'
                           AND coalesce(t.section::text, '') = coalesce(q.pinned->>'section', ''))
                         THEN 'The category changed since the agent asked.'
               END AS stale_why
        FROM blog_request q
        JOIN connection c ON c.id = q.connection_id
        JOIN blog b ON b.id = q.blog_id
        LEFT JOIN media m ON m.id = q.media_id
        """;

    private static bool Full(Actor agent) => Levels.Rank(agent.Level!) >= Levels.Rank(Levels.Full);

    /// <summary>An agent trashes a picture: at Full, now; below, a request.</summary>
    public async Task<Outcome> TrashMediaAsync(Actor agent, Guid mediaId, CancellationToken ct)
    {
        var m = await media.GetAsync(agent.BlogId, mediaId, ct);
        if (m is null) return Outcome.Fail("not_found", "No such picture on this blog.");
        if (m.Status != "ready") return Outcome.Fail("invalid_state", $"Only a picture in use goes to the trash; this one is {m.Status}.");
        if (Full(agent))
        {
            var done = await media.TrashAsync(agent, mediaId, ct);
            return done.Ok ? new Outcome(true, null, null, null) : Outcome.Fail(done.Code!, done.Message!);
        }
        return await RequestAsync(agent, "trash_media", mediaId, new { }, new { status = m.Status }, ct);
    }

    /// <summary>An agent changes the blog's title or description: at Full, now; below, a request. Left out stays.</summary>
    public async Task<Outcome> EditSettingsAsync(Actor agent, string? title, string? description, CancellationToken ct)
    {
        title = title?.Trim();
        description = description?.Trim();
        if (title is null && description is null) return Outcome.Fail("invalid_argument", "Give a title, a description, or both.");
        if (title is not null && title.Length is < 1 or > BlogSettings.MaxTitle)
            return Outcome.Fail("invalid_argument", $"The title is 1–{BlogSettings.MaxTitle} characters.");
        if (description is not null && description.Length > BlogSettings.MaxDescription)
            return Outcome.Fail("invalid_argument", $"The description is at most {BlogSettings.MaxDescription} characters.");
        if (Full(agent))
        {
            await db.WriteAsync(tx => ApplySettingsAsync(tx, agent.BlogId, title, description, agent.Value, clock.GetUtcNow()), ct);
            return new Outcome(true, null, null, null);
        }
        var now = await db.ReadAsync(tx => tx.One<(string Title, string Description)>("SELECT title, description FROM blog WHERE id = @BlogId", agent), ct);
        return await RequestAsync(agent, "edit_settings", null, new { title, description }, new { title = now.Title, description = now.Description }, ct);
    }

    /// <summary>
    /// An agent edits a category (its name, description, or its section mark and fields): at Full, now; below, a request
    /// pinned to the category as the agent saw it (docs/specs/taxonomy.md § Sections). A field left null stays.
    /// </summary>
    public async Task<Outcome> EditCategoryAsync(Actor agent, string category, CategoryEdit edit, CancellationToken ct)
    {
        if (edit.IsEmpty) return Outcome.Fail("invalid_argument", "Give a name, a description, or a section field to change.");
        if (Taxonomy.ValidateEdit(edit) is { } bad) return Outcome.Fail("invalid_argument", bad);
        if (await terms.GetCategoryAsync(agent.BlogId, category, ct) is not { } row)
            return Outcome.Fail("not_found", "No such category on this blog.");
        if (Full(agent))
        {
            var o = await terms.EditCategoryAsync(agent, category, edit, ct);
            return o.Ok ? new Outcome(true, null, null, null) : Outcome.Fail(o.Code!, o.Message!);
        }
        // The edit travels as snake_case JSON (Json.Options), the same shape the approval reads back.
        return await RequestAsync(agent, "edit_category", null,
            new { slug = row.Slug, edit = JsonSerializer.SerializeToNode(edit, Json.Options) },
            new { slug = row.Slug, name = row.Name, description = row.Description, section = row.SectionJson }, ct);
    }

    private async Task<Outcome> RequestAsync(Actor agent, string action, Guid? mediaId, object payload, object pinned, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await db.WriteAsync(async tx =>
        {
            var now = clock.GetUtcNow();
            await tx.Exec("""
                INSERT INTO blog_request (id, blog_id, connection_id, action, media_id, payload, pinned, created_at)
                VALUES (@id, @BlogId, @ConnectionId, @action, @mediaId, @payload::jsonb, @pinned::jsonb, @now)
                """, new { id, agent.BlogId, agent.ConnectionId, action, mediaId, payload = JsonSerializer.Serialize(payload),
                pinned = JsonSerializer.Serialize(pinned), now });
            await tx.Event(new EventRow(agent.BlogId, "blog_request", id, "approval_requested", agent.Value, new { action }), now);
            return 0;
        }, ct);
        return new Outcome(false, await GetAsync(agent.BlogId, id, ct), null, null);
    }

    public Task<BlogRequestView?> GetAsync(Guid blogId, Guid id, CancellationToken ct) =>
        db.ReadAsync(tx => tx.One<BlogRequestView>($"{Select} WHERE q.blog_id = @blogId AND q.id = @id", new { blogId, id }), ct);

    /// <summary>An agent's own request, for get_request; another connection's reads as not found.</summary>
    public Task<BlogRequestView?> ForAgentAsync(Guid connectionId, Guid id, CancellationToken ct) =>
        db.ReadAsync(tx => tx.One<BlogRequestView>($"{Select} WHERE q.connection_id = @connectionId AND q.id = @id", new { connectionId, id }), ct);

    /// <summary>The requests waiting for this owner, on every blog they own.</summary>
    public Task<IReadOnlyList<BlogRequestView>> WaitingForAsync(Guid accountId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<BlogRequestView>>(async tx => [.. await tx.Query<BlogRequestView>($"""
            {Select}
            JOIN blog_member o ON o.blog_id = q.blog_id AND o.account_id = @accountId AND o.role = 'owner'
            WHERE q.state = 'waiting' ORDER BY q.created_at
            """, new { accountId })], ct);

    /// <summary>The owner approves or declines; approving applies exactly the request, or turns it stale.</summary>
    public async Task<Outcome> DecideAsync(Actor owner, Guid id, bool approve, string? note, CancellationToken ct)
    {
        if (!owner.IsOwner) return Outcome.Fail("not_found", "No such request on your blogs.");
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note?.Length > PostService.MaxDecisionNote) return Outcome.Fail("invalid_argument", $"A note is at most {PostService.MaxDecisionNote} characters.");
        var q = await GetAsync(owner.BlogId, id, ct);
        if (q is null) return Outcome.Fail("not_found", "No such request on your blogs.");
        if (q.State != "waiting") return Outcome.Fail("invalid_state", $"This request is already {q.State}.");

        var state = !approve ? "declined" : q.StaleWhy is null ? "approved" : "stale";
        if (state == "approved" && q.Action == "trash_media")
        {
            var done = await media.TrashAsync(owner, q.MediaId!.Value, ct);
            if (!done.Ok) state = "stale";
        }
        var changed = await db.WriteAsync(async tx =>
        {
            var now = clock.GetUtcNow();
            if (await tx.Exec("""
                UPDATE blog_request SET state = @state, decided_at = @now, decided_by = @AccountId, decision_note = @note
                WHERE id = @id AND state = 'waiting'
                """, new { state, now, owner.AccountId, note, id }) == 0) return false;
            if (state == "approved" && q.Action == "edit_settings")
            {
                using var p = JsonDocument.Parse(q.Payload);
                string? Str(string name) => p.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                // The agent's change, under the agent's name; the decision event names who let it through.
                await ApplySettingsAsync(tx, owner.BlogId, Str("title"), Str("description"), Actors.Connection(q.ConnectionId), now);
            }
            if (state == "approved" && q.Action == "edit_category")
            {
                using var p = JsonDocument.Parse(q.Payload);
                var slug = p.RootElement.GetProperty("slug").GetString()!;
                var edit = JsonSerializer.Deserialize<CategoryEdit>(p.RootElement.GetProperty("edit").GetRawText(), Json.Options)!;
                await Taxonomy.ApplyCategoryEditAsync(tx, owner.BlogId, slug, edit, Actors.Connection(q.ConnectionId), now);
            }
            await tx.Event(new EventRow(owner.BlogId, "blog_request", id, state switch
            {
                "approved" => "approval_approved",
                "declined" => "approval_declined",
                _ => "approval_stale",
            }, owner.Value, new { action = q.Action }), now);
            return true;
        }, ct);
        if (!changed) return Outcome.Fail("invalid_state", "Someone decided it a moment ago.");
        var after = await GetAsync(owner.BlogId, id, ct);
        return state == "stale"
            ? Outcome.Fail("stale", (q.StaleWhy ?? "What it was about changed since.") + " Nothing was changed.") with { Request = after }
            : new Outcome(true, after, null, null);
    }

    /// <summary>The blog's title and description, changed by <paramref name="actor"/>, with the same event the owner's settings write.</summary>
    private static async Task<int> ApplySettingsAsync(Tx tx, Guid blogId, string? title, string? description, string actor, DateTimeOffset now)
    {
        var before = await tx.One<(string Title, string Description)>("SELECT title, description FROM blog WHERE id = @blogId FOR UPDATE", new { blogId });
        var changed = new Dictionary<string, object>();
        if (title is not null && title != before.Title) changed["title"] = new { from = before.Title, to = title };
        if (description is not null && description != before.Description) changed["description"] = new { from = before.Description, to = description };
        if (changed.Count == 0) return 0;
        await tx.Exec("UPDATE blog SET title = coalesce(@title, title), description = coalesce(@description, description) WHERE id = @blogId",
            new { title, description, blogId });
        await tx.Event(new EventRow(blogId, "blog", blogId, "blog_settings_changed", actor, changed), now);
        return 1;
    }
}

/// <summary>The owner's side of requests beyond posts: the Inbox lists them and decides them.</summary>
public static class BlogRequestsApi
{
    public sealed record DecisionBody([property: System.Text.Json.Serialization.JsonPropertyName("note")] string? Note);

    public static void MapBlogRequestsApi(this WebApplication app)
    {
        var api = AgentBlog.Auth.SignIn.MapOwnerApi(app);

        api.MapGet("/blog-requests", async (HttpContext ctx, BlogRequests requests) => Results.Json(new
        {
            requests = (await requests.WaitingForAsync(AgentBlog.Auth.SignIn.Current(ctx).AccountId, ctx.RequestAborted)).Select(Json),
        }));

        api.MapPost("/blog-requests/{id:guid}/{decision:regex(^(approve|decline)$)}", async (HttpContext ctx, Guid id, string decision, Db db,
            BlogRequests requests) =>
        {
            DecisionBody? body = null;
            if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
            {
                try { body = await ctx.Request.ReadFromJsonAsync<DecisionBody>(ctx.RequestAborted); }
                catch (JsonException) { return AgentBlog.Auth.SignIn.Problem(400, "invalid_argument", "The body is JSON: { \"note\" }, optional."); }
            }
            var account = AgentBlog.Auth.SignIn.Current(ctx).AccountId;
            var blogId = await db.ReadAsync(tx => tx.One<Guid?>("""
                SELECT q.blog_id FROM blog_request q JOIN blog_member m ON m.blog_id = q.blog_id AND m.account_id = @account AND m.role = 'owner'
                WHERE q.id = @id
                """, new { id, account }), ctx.RequestAborted);
            if (blogId is null) return AgentBlog.Auth.SignIn.Problem(404, "not_found", "No such request on your blogs.");
            var o = await requests.DecideAsync(Actor.Owner(blogId.Value, account), id, decision == "approve", body?.Note, ctx.RequestAborted);
            return o.Done ? Results.Json(new { request = o.Request is { } q ? Json(q) : null })
                : AgentBlog.Auth.SignIn.Problem(o.Code is "stale" or "invalid_state" ? 409 : o.Code == "not_found" ? 404 : 400, o.Code!, o.Message!);
        });
    }

    public static object Json(BlogRequestView q) => new
    {
        id = q.Id, blog = q.BlogId, agent = q.ConnectionLabel, action = q.Action, state = q.State, created_at = q.CreatedAt,
        media = q.MediaId is { } m ? new { id = m, filename = q.MediaFilename, path = q.MediaPath } : null,
        change = System.Text.Json.Nodes.JsonNode.Parse(q.Payload), was = System.Text.Json.Nodes.JsonNode.Parse(q.Pinned),
        why_not = q.StaleWhy, note = q.DecisionNote,
    };
}
