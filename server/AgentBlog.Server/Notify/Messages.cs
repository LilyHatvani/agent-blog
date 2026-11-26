using System.Text.Json.Nodes;
using AgentBlog.Database;
using AgentKit.Channels;

namespace AgentBlog.Notify;

/// <summary>One event the owner can be told about.</summary>
public sealed record NotifyEvent(string Kind, string Label, bool Default);

/// <summary>
/// What the owner can be told (docs/specs/notifications.md § Events). The defaults are what waits on the owner or
/// changes who can write; a post going live is opt-in. Events the owner caused themselves are never sent back to them,
/// except a new connection, which is a change of who can write.
/// </summary>
public static class NotifyCatalog
{
    public static readonly IReadOnlyList<NotifyEvent> Events =
    [
        new("approval_requested", "An agent asks you to decide (publish, schedule, edit, trash)", true),
        new("post_submitted", "An agent or a writer submits a draft for review", true),
        new("schedule_dropped", "A scheduled post didn't go live", true),
        new("connection_created", "A new agent is connected", true),
        new("invitation_accepted", "Someone you invited joins the blog", true),
        new("channel_disabled", "Another notification channel stopped working", true),
        new("cdn_purge_refused", "Cloudflare stopped taking purges of trashed pictures", true),
        new("announcement_waiting", "An announcement waits for your tap (an agent published the post or wrote the text, or it goes to Patreon, which you post yourself)", true),
        new("announcement_failed", "An announcement failed", true),
        new("announcement_unconfirmed", "An announcement may not have gone out: look, then send again or mark it", true),
        new("post_published", "A post went live that you didn't publish (an agent, an editor, or a schedule)", false),
    ];

    public static readonly string[] Kinds = [.. Events.Select(e => e.Kind)];

    public static readonly string[] Defaults = [.. Events.Where(e => e.Default).Select(e => e.Kind)];

    /// <summary>
    /// Whether an event is sent at all, before any channel's choice. <paramref name="ownerActor"/> is the blog owner's
    /// actor: what the owner did is never sent back to them; what a writer or an editor did is, as an agent's is.
    /// </summary>
    public static bool Sendable(string kind, string actor, string? ownerActor) => kind switch
    {
        "connection_created" or "schedule_dropped" or "channel_disabled" or "cdn_purge_refused" => true,
        "announcement_waiting" or "announcement_failed" or "announcement_unconfirmed" => actor == Actors.System,
        _ => Kinds.Contains(kind) && actor != ownerActor,
    };
}

/// <summary>An event with what a message about it needs, read when the delivery is sent.</summary>
public sealed record EventFacts(long EventId, Guid BlogId, string Kind, string Actor, string ActorLabel, DateTime At,
    string BlogTitle, Guid TargetId, string? PostTitle, Guid? PostId, string? PostStatus, string? Action, string? RequestState,
    string? Reason, string? Level, string? TargetLabel)
{
    /// <summary>Why this message would be wrong to send now (the moment passed), or null: judged when it is sent.</summary>
    public string? Stale => Kind switch
    {
        "approval_requested" when RequestState != "waiting" => $"The request was {RequestState ?? "removed"} before this went out.",
        "post_submitted" when PostStatus != "review" => "The post left review before this went out.",
        "post_published" when PostStatus != "live" => "The post wasn't live any more when this went out.",
        "announcement_waiting" when RequestState != "waiting" => $"The announcement was {RequestState ?? "removed"} before this went out.",
        "announcement_failed" when RequestState != "failed" => $"The announcement was {RequestState ?? "removed"} before this went out.",
        "announcement_unconfirmed" when RequestState != "unconfirmed" => $"The announcement was {RequestState ?? "removed"} before this went out.",
        _ => null,
    };
}

/// <summary>
/// The text of each message: fixed templates, never a model. A post title is agent-written, so it goes out only at
/// <c>detail = full</c>, and AgentKit.Channels escapes it per destination. <c>minimal</c> says what happened and no more,
/// for a channel other people can read (a shared Discord, a public ntfy topic).
/// </summary>
public static class Messages
{
    public static ChannelMessage For(EventFacts e, string detail, string? adminUrl)
    {
        var full = detail == "full";
        var title = full && e.PostTitle is { } t ? $"“{t}”" : "a post";
        var (head, body, path) = e.Kind switch
        {
            "approval_requested" when e.Action is "trash_media" or "edit_settings" =>
                ($"{e.ActorLabel} asks to {Verb(e.Action)}", "Open the Inbox to see it and decide.", "#/"),
            "approval_requested" => ($"{e.ActorLabel} asks to {Verb(e.Action)} {title}", "Open the Inbox to see the change and decide.", "#/"),
            "post_submitted" => ($"{e.ActorLabel} submitted {title} for review", "It waits in Posts until you publish it or send it back.", $"#/posts/{e.PostId}"),
            "schedule_dropped" => ($"{Capital(title)} didn't go live", full && e.Reason is { } r ? r + " It's a draft again." : "It's a draft again.", $"#/posts/{e.PostId}"),
            "connection_created" => ($"A new agent is connected: {e.TargetLabel ?? e.ActorLabel}", $"{e.ActorLabel} connected it. It starts at {e.Level ?? "propose"}. Agents shows it, and revokes it.", "#/agents"),
            "invitation_accepted" => ($"{e.ActorLabel} joined {e.BlogTitle}", $"As {(e.Level == "editor" ? "an editor" : "a writer")}. People shows everyone on the blog.", "#/account/people"),
            "post_published" => ($"{Capital(title)} is live", e.ActorLabel == "System" ? "It went live on schedule." : $"{e.ActorLabel} published it.", $"#/posts/{e.PostId}"),
            "announcement_waiting" => ($"{Capital(title)} waits to be announced on {e.TargetLabel ?? "an account"}",
                "An agent published it or wrote what to say. Open the post, read it, and tap Announce.", $"#/posts/{e.PostId}"),
            "announcement_failed" => ($"Announcing {title} on {e.TargetLabel ?? "an account"} failed",
                full && e.Reason is { } why ? why : "Open the post to see why.", $"#/posts/{e.PostId}"),
            "announcement_unconfirmed" => ($"Did {title} reach {e.TargetLabel ?? "an account"}?",
                "Its answer was lost. Look there, then tap Send again or It's there.", $"#/posts/{e.PostId}"),
            "cdn_purge_refused" => ("Cloudflare stopped taking purges", e.Reason ?? "Trashed pictures can stay at its edge.", "#/media"),
            "channel_disabled" => ($"Notifications to {e.TargetLabel ?? "a channel"} stopped", e.Reason ?? "It answered that it no longer exists.", "#/notifications"),
            _ => ($"Something happened on {e.BlogTitle}", "", "#/history"),
        };
        var link = adminUrl is null ? null : new Uri(adminUrl + path);
        var data = new JsonObject
        {
            ["event_id"] = e.EventId,
            ["blog"] = e.BlogId,
            ["at"] = new DateTimeOffset(DateTime.SpecifyKind(e.At, DateTimeKind.Utc)).ToString("O"),
            ["actor"] = e.Actor,
            ["post"] = e.PostId,
            ["action"] = e.Action,
        };
        return new ChannelMessage(e.Kind, head, body, link, data);
    }

    /// <summary>A test message, so the owner sees a channel work before anything happens.</summary>
    public static ChannelMessage Test(string blogTitle, string channelLabel, string? adminUrl) =>
        new("test", $"Test from {blogTitle}", $"Notifications to {channelLabel} work.", adminUrl is null ? null : new Uri(adminUrl + "#/notifications"));

    private static string Verb(string? action) => action switch
    {
        "edit" => "change",
        "publish" => "publish",
        "schedule" => "schedule",
        "unpublish" => "unpublish",
        "trash" => "trash",
        "restore" => "restore",
        "trash_media" => "trash a picture",
        "edit_settings" => "change the blog's title or description",
        _ => "change",
    };

    private static string Capital(string s) => s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;
}
