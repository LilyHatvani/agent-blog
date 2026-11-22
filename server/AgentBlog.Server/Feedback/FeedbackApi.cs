using System.Text.Json.Serialization;
using AgentBlog.Auth;
using AgentBlog.Database;
using AgentBlog.Posts;

namespace AgentBlog.Feedback;

/// <summary>The owner's side of the brief and notes (docs/specs/feedback.md): read and write, only with a session.</summary>
public static class FeedbackApi
{
    public sealed record BriefBody(
        [property: JsonPropertyName("audience")] string? Audience,
        [property: JsonPropertyName("voice")] string? Voice,
        [property: JsonPropertyName("avoid")] string? Avoid,
        [property: JsonPropertyName("examples")] Guid[]? Examples);

    public sealed record NoteBody(
        [property: JsonPropertyName("note")] string? Note,
        [property: JsonPropertyName("post")] Guid? Post);

    public static void MapFeedbackApi(this WebApplication app)
    {
        var api = app.MapOwnerApi();

        api.MapGet("/blogs/{blogId:guid}/brief", async (HttpContext ctx, Guid blogId, OwnerFeedback feedback, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var (brief, examples) = await feedback.BriefAsync(blogId, ctx.RequestAborted);
            return Results.Json(BriefJson(brief, examples));
        });

        api.MapPut("/blogs/{blogId:guid}/brief", async (HttpContext ctx, Guid blogId, BriefBody body, OwnerFeedback feedback, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var error = await feedback.SaveBriefAsync(Actor.Owner(blogId, SignIn.Current(ctx).AccountId), body.Audience, body.Voice, body.Avoid,
                body.Examples, ctx.RequestAborted);
            if (error is not null) return SignIn.Problem(400, "invalid_argument", error);
            var (brief, examples) = await feedback.BriefAsync(blogId, ctx.RequestAborted);
            return Results.Json(BriefJson(brief, examples));
        });

        api.MapGet("/blogs/{blogId:guid}/notes", async (HttpContext ctx, Guid blogId, OwnerFeedback feedback, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var notes = await feedback.NotesAsync(blogId, current: false, null, 100, ctx.RequestAborted);
            return Results.Json(new { notes = notes.Select(NoteJson), sent_to_agents = OwnerFeedback.RecentNotes });
        });

        api.MapPost("/blogs/{blogId:guid}/notes", async (HttpContext ctx, Guid blogId, NoteBody body, OwnerFeedback feedback, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var (note, error) = await feedback.AddNoteAsync(Actor.Owner(blogId, SignIn.Current(ctx).AccountId), body.Note, body.Post, ctx.RequestAborted);
            return error is null ? Results.Json(new { note = NoteJson(note!) }) : SignIn.Problem(400, "invalid_argument", error);
        });

        api.MapPost("/blogs/{blogId:guid}/notes/{noteId:guid}/retire", async (HttpContext ctx, Guid blogId, Guid noteId, OwnerFeedback feedback, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            return await feedback.RetireAsync(Actor.Owner(blogId, SignIn.Current(ctx).AccountId), noteId, ctx.RequestAborted)
                ? Results.NoContent()
                : SignIn.Problem(404, "not_found", "No such note in force on this blog.");
        });
    }

    internal static object BriefJson(Brief b, IReadOnlyList<BriefExample> examples) => new
    {
        audience = b.Audience, voice = b.Voice, avoid = b.Avoid, updated_at = b.UpdatedAt,
        examples = examples.Select(e => new { id = e.Id, slug = e.Slug, title = e.Title, status = e.Status }),
        limits = new { audience = OwnerFeedback.MaxAudience, voice = OwnerFeedback.MaxVoice, avoid = OwnerFeedback.MaxAvoid, examples = OwnerFeedback.MaxExamples },
    };

    private static object NoteJson(NoteView n) => new
    {
        id = n.Id, kind = n.Kind, note = n.Note, post = n.PostId, post_title = n.PostTitle, request = n.RequestId, action = n.Action,
        at = n.CreatedAt, retired_at = n.RetiredAt,
    };

    private static IResult NotFound() => SignIn.Problem(404, "not_found", "No such blog of yours.");

    private static Task<bool> OwnsAsync(Db db, HttpContext ctx, Guid blogId) => db.ReadAsync(tx => tx.One<bool>("""
        SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @blogId AND account_id = @accountId AND role = 'owner')
        """, new { blogId, accountId = SignIn.Current(ctx).AccountId }), ctx.RequestAborted);
}
