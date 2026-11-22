using System.Text.Json.Serialization;
using AgentBlog.Auth;
using AgentBlog.Database;
using AgentBlog.Posts;

namespace AgentBlog.Announce;

/// <summary>The owner's side of announcing (docs/specs/announce.md): accounts, the wait, and each post's announcements. Session only.</summary>
public static class AnnounceApi
{
    public sealed record AccountBody(
        [property: JsonPropertyName("network")] string? Network,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("handle")] string? Handle,
        [property: JsonPropertyName("password")] string? Password,
        [property: JsonPropertyName("instance")] string? Instance,
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("chat")] string? Chat,
        [property: JsonPropertyName("api_key")] string? ApiKey,
        [property: JsonPropertyName("api_secret")] string? ApiSecret,
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("access_secret")] string? AccessSecret,
        [property: JsonPropertyName("auto")] bool? Auto,
        [property: JsonPropertyName("enabled")] bool? Enabled);

    public sealed record SettingsBody([property: JsonPropertyName("delay_minutes")] int? DelayMinutes);

    public sealed record TextBody([property: JsonPropertyName("text")] string? Text);

    public static void MapAnnounceApi(this WebApplication app)
    {
        var api = app.MapOwnerApi();

        api.MapGet("/blogs/{blogId:guid}/announce", async (HttpContext ctx, Guid blogId, SocialAccounts accounts, Announcer announcer, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var ct = ctx.RequestAborted;
            var list = await accounts.ListAsync(blogId, ct);
            var waiting = await announcer.WaitingAsync(blogId, ct);
            var titles = await TitlesAsync(db, waiting.Select(w => w.PostId).Distinct().ToArray(), ct);
            return Results.Json(new
            {
                available = accounts.On,
                reason = accounts.OffReason,
                delay_minutes = await announcer.DelayAsync(blogId, ct),
                networks = SocialAccounts.Networks,
                accounts = list.Select(AccountJson),
                // What needs the owner: waiting for a tap, unconfirmed, failed.
                attention = waiting.Select(w => new
                {
                    id = w.Id, post = w.PostId, post_title = titles.GetValueOrDefault(w.PostId), account = w.AccountId,
                    account_label = list.FirstOrDefault(a => a.Id == w.AccountId)?.Label, state = w.State, why_waiting = w.WhyWaiting,
                    error = w.Error, updated_at = w.UpdatedAt,
                }),
            });
        });

        api.MapPut("/blogs/{blogId:guid}/announce", async (HttpContext ctx, Guid blogId, SettingsBody body, Announcer announcer, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            if (body.DelayMinutes is not { } minutes) return SignIn.Problem(400, "invalid_argument", "delay_minutes is required.");
            return await announcer.SetDelayAsync(Owner(ctx, blogId), minutes, ctx.RequestAborted) is { } error
                ? SignIn.Problem(400, "invalid_argument", error)
                : Results.Json(new { delay_minutes = minutes });
        });

        api.MapPost("/blogs/{blogId:guid}/social-accounts", async (HttpContext ctx, Guid blogId, AccountBody body, SocialAccounts accounts, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var (account, error) = await accounts.CreateAsync(Owner(ctx, blogId), new NewSocialAccount(body.Network ?? "", body.Label, body.Handle,
                body.Password, body.Instance, body.Token, body.Url, body.Chat, body.ApiKey, body.ApiSecret, body.AccessToken, body.AccessSecret),
                ctx.RequestAborted);
            return account is null
                ? SignIn.Problem(accounts.On ? 400 : 409, accounts.On ? "invalid_argument" : "secrets_off", error!)
                : Results.Json(new { account = AccountJson(account) }, statusCode: 201);
        });

        api.MapPatch("/blogs/{blogId:guid}/social-accounts/{accountId:guid}", async (HttpContext ctx, Guid blogId, Guid accountId, AccountBody body,
            SocialAccounts accounts, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            var row = await accounts.ChangeAsync(Owner(ctx, blogId), accountId, body.Label, body.Auto, body.Enabled, ctx.RequestAborted);
            return row is null ? SignIn.Problem(404, "not_found", "No such account, or a name over 100 characters.") : Results.Json(new { account = AccountJson(row) });
        });

        api.MapDelete("/blogs/{blogId:guid}/social-accounts/{accountId:guid}", async (HttpContext ctx, Guid blogId, Guid accountId, SocialAccounts accounts, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            return await accounts.RemoveAsync(Owner(ctx, blogId), accountId, ctx.RequestAborted)
                ? Results.NoContent() : SignIn.Problem(404, "not_found", "No such account.");
        });

        // A post's announcements: one per account, made or not, with the text each network would send and its count.
        api.MapGet("/posts/{postId:guid}/announcements", async (HttpContext ctx, Guid postId, SocialAccounts accounts, Announcer announcer, Db db) =>
        {
            if (await BlogOfAsync(db, ctx, postId) is not { } blogId) return NotFound();
            var ct = ctx.RequestAborted;
            var post = await announcer.PostAsync(blogId, postId, ct);
            if (post is null) return NotFound();
            var rows = await announcer.ForPostAsync(blogId, postId, ct);
            var list = new List<object>();
            foreach (var account in await accounts.ListAsync(blogId, ct))
            {
                var row = rows.FirstOrDefault(r => r.AccountId == account.Id);
                var channel = await accounts.ChannelAsync(account, ct);
                Composed? composed = channel is null ? null : (await announcer.BuildAsync(channel, post, row?.Text, row?.Id ?? Guid.Empty, ct)).Composed;
                list.Add(new
                {
                    account = AccountJson(account),
                    id = row?.Id, state = row?.State ?? "none", why_waiting = row?.WhyWaiting, text = row?.Text,
                    text_by = row?.TextBy, text_by_label = row?.TextByLabel, due_at = row?.DueAt, attempts = row?.Attempts ?? 0,
                    url = row?.RemoteUrl, error = row?.Error, notes = row?.Notes ?? [], sent_at = row?.SentAt,
                    composed = composed is null ? null : ComposedJson(composed),
                    credentials_open = channel is not null,
                });
            }
            return Results.Json(new { post = postId, status = post.Status, link = announcer.LinkOf(post), announcements = list });
        });

        // The count as the owner types, without saving: the network's own rules, no network call.
        api.MapPost("/posts/{postId:guid}/announcements/{accountId:guid}/compose", async (HttpContext ctx, Guid postId, Guid accountId, TextBody body,
            SocialAccounts accounts, Announcer announcer, Db db) =>
        {
            if (await BlogOfAsync(db, ctx, postId) is not { } blogId) return NotFound();
            var post = await announcer.PostAsync(blogId, postId, ctx.RequestAborted);
            var account = await accounts.GetAsync(blogId, accountId, ctx.RequestAborted);
            if (post is null || account is null) return NotFound();
            if (await accounts.ChannelAsync(account, ctx.RequestAborted) is not { } channel)
                return SignIn.Problem(409, "credentials", "The account's credentials don't open: remove it and add it again.");
            var text = string.IsNullOrWhiteSpace(body.Text) ? null : body.Text.Trim();
            return Results.Json(ComposedJson((await announcer.BuildAsync(channel, post, text, Guid.Empty, ctx.RequestAborted)).Composed));
        });

        api.MapPut("/posts/{postId:guid}/announcements/{accountId:guid}", async (HttpContext ctx, Guid postId, Guid accountId, TextBody body,
            Announcer announcer, Db db) =>
        {
            if (await BlogOfAsync(db, ctx, postId) is not { } blogId) return NotFound();
            var (row, composed, error) = await announcer.WriteAsync(Owner(ctx, blogId), postId, accountId, body.Text, ctx.RequestAborted);
            return row is null
                ? SignIn.Problem(400, "invalid_argument", error ?? "That can't be saved.")
                : Results.Json(new { id = row.Id, state = row.State, text = row.Text, composed = ComposedJson(composed!) });
        });

        api.MapPost("/posts/{postId:guid}/announcements/{accountId:guid}/{action:regex(^(send|resend|done|cancel)$)}",
            async (HttpContext ctx, Guid postId, Guid accountId, string action, Announcer announcer, Db db) =>
        {
            if (await BlogOfAsync(db, ctx, postId) is not { } blogId) return NotFound();
            return await announcer.ActAsync(Owner(ctx, blogId), postId, accountId, action, ctx.RequestAborted) is { } error
                ? SignIn.Problem(409, "not_now", error)
                : Results.Json(new { ok = true });
        });
    }

    private static object AccountJson(SocialAccountRow a) => new
    {
        id = a.Id, network = a.Network, label = a.Label, hint = SocialAccounts.Hint(a), auto = a.Auto, enabled = a.Enabled,
        last_error = a.LastError, failing_since = a.FailingSince, created_at = a.CreatedAt,
    };

    private static object ComposedJson(Composed c) => new
    {
        text = c.Text, length = c.Length, max = c.MaxLength, unit = c.Unit, problems = c.Problems, fits = c.Fits(), is_default = c.IsDefault,
    };

    private static async Task<Dictionary<Guid, string>> TitlesAsync(Db db, Guid[] ids, CancellationToken ct) => ids.Length == 0 ? [] :
        (await db.ReadAsync(tx => tx.Query<(Guid Id, string Title)>("""
            SELECT p.id, r.title FROM post p JOIN post_revision r ON r.id = p.current_revision_id WHERE p.id = ANY (@ids)
            """, new { ids }), ct)).ToDictionary(x => x.Id, x => x.Title);

    private static Actor Owner(HttpContext ctx, Guid blogId) => Actor.Owner(blogId, SignIn.Current(ctx).AccountId);

    private static IResult NotFound() => SignIn.Problem(404, "not_found", "No such blog or post of yours.");

    private static Task<bool> OwnsAsync(Db db, HttpContext ctx, Guid blogId) => db.ReadAsync(tx => tx.One<bool>("""
        SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @blogId AND account_id = @accountId AND role = 'owner')
        """, new { blogId, accountId = SignIn.Current(ctx).AccountId }), ctx.RequestAborted);

    private static Task<Guid?> BlogOfAsync(Db db, HttpContext ctx, Guid postId) => db.ReadAsync(tx => tx.One<Guid?>("""
        SELECT p.blog_id FROM post p JOIN blog_member m ON m.blog_id = p.blog_id AND m.account_id = @accountId AND m.role = 'owner'
        WHERE p.id = @postId
        """, new { postId, accountId = SignIn.Current(ctx).AccountId }), ctx.RequestAborted);
}
