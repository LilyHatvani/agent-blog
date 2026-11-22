using AgentKit.Secrets;

namespace AgentBlog.Secrets;

/// <summary>
/// The blog's secrets at rest (agent-kit's <see cref="Sealer"/>, Relay's format): channel webhook URLs, tokens, signing
/// secrets, and the instance's VAPID private key. The keyring is <c>Blog__Secrets__MasterKey</c> (and
/// <c>Blog__Secrets__PreviousKeys</c> while rotating); without it, nothing that needs a secret can be set up, and
/// everything else runs. Each value is sealed with associated data naming its row, so a value copied to another row
/// doesn't open.
/// </summary>
public static class BlogSecrets
{
    /// <summary>The label is part of every key id stored in a row: never change it.</summary>
    public static readonly MasterKeyOptions Options = new("Blog:Secrets", "agentblog-master-key-id:", "openssl rand -hex 32 prints one");

    public const int MaxValueBytes = 8192;

    public static string OffReason =>
        $"Secrets can't be stored yet: set {Options.Variable} on the server (32 random bytes: openssl rand -hex 32) and redeploy.";

    public static byte[] ForChannel(Guid blogId, Guid channelId, string name) =>
        Sealer.AssociatedData("agentblog-channel-secret:v1", blogId.ToString(), channelId.ToString(), name);

    public static byte[] ForBlog(Guid blogId, string name) => Sealer.AssociatedData("agentblog-blog-secret:v1", blogId.ToString(), name);

    public static byte[] ForInstance(string name) => Sealer.AssociatedData("agentblog-instance-secret:v1", name);
}
