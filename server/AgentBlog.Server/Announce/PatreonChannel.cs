using AgentKit.Social;

namespace AgentBlog.Announce;

/// <summary>
/// Patreon (docs/specs/announce.md § Patreon): its API reads posts but can't make one, so nothing is ever sent. The
/// channel only composes: the words with the post's link under them, for the owner to copy into a Patreon post. An
/// announcement to it waits for the owner (why_waiting 'manual') until they tap It's there.
/// </summary>
public sealed class PatreonChannel(Guid accountId) : ISocialChannel
{
    public const int MaxText = 10_000;

    public string Network => "patreon";

    public string Id => $"patreon:{accountId:D}";

    public SocialIdempotency Idempotency { get; } = new(SocialIdempotencyKind.ReRead, null, "Nothing is sent: the owner posts it by hand.");

    public SocialComposition Compose(SocialPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        var words = (post.Text ?? "").Trim();
        var text = post.Link is null ? words : words.Length == 0 ? post.Link.AbsoluteUri : $"{words}\n\n{post.Link.AbsoluteUri}";
        var problems = new List<string>();
        if (words.Length == 0) problems.Add("There is nothing to say.");
        if (text.Length > MaxText) problems.Add($"It is {text.Length} characters; at most {MaxText}.");
        return new SocialComposition(text, text.Length, MaxText, "characters", problems);
    }

    public SocialAttempt BeginAttempt(SocialPost post) => throw new InvalidOperationException(NotSent);

    public Task<SocialPostResult> SendAsync(SocialPost post, SocialAttempt attempt, CancellationToken cancellationToken = default) =>
        Task.FromResult(SocialPostResult.Failed(SocialFailureKind.Rejected, NotSent));

    public const string NotSent = "Patreon has no way to post for you: copy the words, post them on Patreon, then tap It's there.";
}
