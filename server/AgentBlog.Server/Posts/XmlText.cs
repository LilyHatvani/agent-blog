using System.Text;
using System.Text.RegularExpressions;

namespace AgentBlog.Posts;

/// <summary>
/// Text the XML feeds carry (RSS, Atom, the sitemap). XML 1.0 carries tab, LF, CR, U+0020–U+D7FF, U+E000–U+FFFD and
/// U+10000–U+10FFFF (§2.2 Char; in a .NET string the last range is a surrogate pair) and nothing else: not U+0000–U+001F
/// but those three, not a lone surrogate, not U+FFFE or U+FFFF. Where a person types text it is refused naming the
/// character (a title, a section's fields); where it was stored before or arrives from elsewhere (an import, the feed
/// writer) the character is dropped. Tab, LF and CR are carried: a Markdown intro keeps its paragraphs. A field that is
/// one line (a title, a name) refuses them for that reason, not for XML's.
/// </summary>
public static partial class XmlText
{
    /// <summary>The first character XML 1.0 can't carry, as its UTF-16 code unit, or null when there is none.</summary>
    public static int? FirstUncarryable(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            else if (!IsCarryable(c)) return c;
        }
        return null;
    }

    /// <summary>The string with every character XML 1.0 can't carry dropped. Valid surrogate pairs (an emoji, say) stay whole.</summary>
    public static string Carryable(string s)
    {
        if (FirstUncarryable(s) is null) return s;
        var clean = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                clean.Append(c).Append(s[i + 1]);
                i++;
            }
            else if (IsCarryable(c)) clean.Append(c);
        }
        return clean.ToString();
    }

    /// <summary>
    /// The first <paramref name="max"/> UTF-16 units of the text, or one fewer when the cut would land between the two
    /// halves of a surrogate pair (an emoji, say): a cut is always on a code point's boundary, so it never leaves half a
    /// character behind for the feeds to drop or a reader to see as a broken glyph. Text no longer than
    /// <paramref name="max"/> comes back as it is.
    /// </summary>
    public static string Cut(string s, int max)
    {
        if (s.Length <= max) return s;
        if (max <= 0) return "";
        return s[..(char.IsHighSurrogate(s[max - 1]) && char.IsLowSurrogate(s[max]) ? max - 1 : max)];
    }

    /// <summary>A single UTF-16 unit XML 1.0 carries on its own (a surrogate only ever as half of a pair).</summary>
    private static bool IsCarryable(char c) =>
        c is '\t' or '\n' or '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD);

    /// <summary>
    /// Whether the text has a tab, LF or CR: what makes a one-line field more than one line. U+2028 and U+2029 are text
    /// XML carries; a title keeps them (the JSON-LD escapes them).
    /// </summary>
    public static bool BreaksLine(string s) => s.AsSpan().IndexOfAny(LineBreaks) >= 0;

    private const string LineBreaks = "\t\n\r";

    /// <summary>
    /// The text on one line: every run of spaces around a tab or line break becomes one space, and the ends are trimmed
    /// (an imported title that wrapped or carried a tab). Spaces elsewhere are left as they are.
    /// </summary>
    public static string OneLine(string s) => BreakRun().Replace(s, " ").Trim();

    [GeneratedRegex("[ \t\n\r]*[\t\n\r][ \t\n\r]*")]
    private static partial Regex BreakRun();

    /// <summary>
    /// What's wrong with a field's text for the feeds, or null: a character XML can't carry (named by its code), or, in a
    /// one-line field, a tab or line break.
    /// </summary>
    public static string? Problem(string field, string? value, bool oneLine)
    {
        if (value is null) return null;
        if (FirstUncarryable(value) is { } c)
            return $"{field} carries U+{c:X4}, a character XML can't carry, so the feeds couldn't either; remove it.";
        if (oneLine && BreaksLine(value)) return $"{field} is one line: take out the tab or line break.";
        return null;
    }
}
