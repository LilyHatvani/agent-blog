namespace AgentBlog.Posts;

/// <summary>
/// An old_address target is always a path on this blog (docs/specs/old-addresses.md): it starts with '/', never a
/// protocol-relative '//host', and never a backslash or control character a browser would read as a scheme or host —
/// '/\host' reads as '//host' and sends readers elsewhere. The table's CHECK refuses these too; every writer and the
/// export restore check first, so an unsafe target is never stored nor served.
/// </summary>
public static class OldAddress
{
    public static bool SafeTarget(string? target) =>
        target is { Length: > 0 and <= 500 }
        && target[0] == '/'
        && (target.Length == 1 || (target[1] != '/' && target[1] != '\\'))
        && !target.Any(c => c == '\\' || char.IsControl(c) || char.IsWhiteSpace(c));
}
