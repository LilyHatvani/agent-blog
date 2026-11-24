using AgentBlog.Posts;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// What the feeds can carry (XML 1.0 §2.2, the Char production): tab, LF, CR, U+0020–U+D7FF, U+E000–U+FFFD and
/// U+10000–U+10FFFF, and nothing else. A character outside it is named where a person types it and dropped where it was
/// stored before or comes from elsewhere; a character inside it is kept, whole. And a cut never splits a surrogate pair.
/// </summary>
public sealed class XmlTextTests
{
    /// <summary>The code point as .NET holds it: one UTF-16 unit, or a surrogate pair above U+FFFF.</summary>
    private static string Of(int codePoint) => codePoint <= 0xFFFF ? ((char)codePoint).ToString() : char.ConvertFromUtf32(codePoint);

    [Theory]
    // The three control characters XML carries.
    [InlineData(0x9)]
    [InlineData(0xA)]
    [InlineData(0xD)]
    // U+0020–U+D7FF, at both ends and in between (DEL and NEL are Chars too, however rarely wanted).
    [InlineData(0x20)]
    [InlineData(0x7E)]
    [InlineData(0x7F)]
    [InlineData(0x85)]
    [InlineData(0x200F)]
    [InlineData(0xD7FF)]
    // U+E000–U+FFFD.
    [InlineData(0xE000)]
    [InlineData(0xFEFF)]
    [InlineData(0xFFFD)]
    // U+10000–U+10FFFF: a surrogate pair in a .NET string.
    [InlineData(0x10000)]
    [InlineData(0x1F600)]
    [InlineData(0x10FFFF)]
    public void A_character_in_the_Char_production_is_carried_whole(int codePoint)
    {
        var s = "a" + Of(codePoint) + "b";
        Assert.Null(XmlText.FirstUncarryable(s));
        Assert.Equal(s, XmlText.Carryable(s));
        Assert.Null(XmlText.Problem("title", s, oneLine: false));
    }

    [Theory]
    // The C0 controls but tab, LF and CR.
    [InlineData(0x0)]
    [InlineData(0x1)]
    [InlineData(0x8)]
    [InlineData(0xB)]
    [InlineData(0xC)]
    [InlineData(0xE)]
    [InlineData(0x1F)]
    // Surrogates on their own: half a character, never a character.
    [InlineData(0xD800)]
    [InlineData(0xDBFF)]
    [InlineData(0xDC00)]
    [InlineData(0xDFFF)]
    // The two noncharacters at the top of the BMP.
    [InlineData(0xFFFE)]
    [InlineData(0xFFFF)]
    public void A_code_point_outside_the_Char_production_is_named_and_dropped(int codePoint)
    {
        var s = "a" + (char)codePoint + "b";
        Assert.Equal(codePoint, XmlText.FirstUncarryable(s));
        Assert.Equal("ab", XmlText.Carryable(s));
        Assert.Equal($"title carries U+{codePoint:X4}, a character XML can't carry, so the feeds couldn't either; remove it.",
            XmlText.Problem("title", s, oneLine: false));
    }

    [Fact]
    public void A_surrogate_is_carried_only_as_the_first_half_then_the_second_of_one_pair()
    {
        var pair = char.ConvertFromUtf32(0x1F600);
        // Reversed: a low surrogate then a high one is two lone halves, both dropped.
        Assert.Equal("ab", XmlText.Carryable("a" + pair[1] + pair[0] + "b"));
        Assert.Equal(pair[1], XmlText.FirstUncarryable("a" + pair[1] + pair[0] + "b"));
        // A high surrogate at the very end has no second half.
        Assert.Equal("ab", XmlText.Carryable("ab" + pair[0]));
        Assert.Equal(pair[0], XmlText.FirstUncarryable("ab" + pair[0]));
        // Two highs then one low: the first high is alone, the second pairs.
        Assert.Equal("a" + pair, XmlText.Carryable("a" + pair[0] + pair));
        // A real pair beside a dropped control character stays whole.
        Assert.Equal(pair + "x", XmlText.Carryable(pair + (char)1 + "x"));
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        var smile = char.ConvertFromUtf32(0x1F600);
        var s = "ab" + smile + "c"; // a b [high low] c
        Assert.Equal("ab", XmlText.Cut(s, 3)); // the cut would fall between the halves: it backs off to before the pair
        Assert.Equal("ab" + smile, XmlText.Cut(s, 4));
        Assert.Equal("a", XmlText.Cut(s, 1));
        Assert.Equal(s, XmlText.Cut(s, 5));
        Assert.Equal(s, XmlText.Cut(s, 50));
        Assert.Equal("", XmlText.Cut(s, 0));
        Assert.Equal("", XmlText.Cut("", 3));
        // A lone high surrogate at the cut is not a pair: the cut stays where it was asked (dropping it is Carryable's job).
        Assert.Equal("ab" + smile[0], XmlText.Cut("ab" + smile[0] + "c", 3));
    }

    [Fact]
    public void An_imported_title_keeps_its_emoji_whole_when_it_is_cut()
    {
        var smile = char.ConvertFromUtf32(0x1F600);
        // The pair would straddle the 300th unit: the title stops before it.
        Assert.Equal(new string('a', PostService.MaxTitle - 1),
            PostService.ImportedTitle(new string('a', PostService.MaxTitle - 1) + smile + "tail"));
        // A pair that ends exactly at the limit stays.
        var fits = new string('a', PostService.MaxTitle - 2) + smile;
        Assert.Equal(fits, PostService.ImportedTitle(fits));
        // Pairs in the middle stay; lone halves go; the result is one line.
        Assert.Equal("Rain " + smile + " in Lisbon", PostService.ImportedTitle("Rain " + smile + "\uD800 in\tLisbon"));
    }

    [Fact]
    public void An_empty_imported_title_stays_empty_for_the_import_to_refuse()
    {
        Assert.Equal("", PostService.ImportedTitle(""));
        Assert.Equal("", PostService.ImportedTitle(" \t\r\n "));
        Assert.Equal("", PostService.ImportedTitle("\u0001\uDC00"));
    }

    [Fact]
    public void An_imported_excerpt_is_cut_on_a_code_point_boundary_and_ends_with_an_ellipsis()
    {
        var smile = char.ConvertFromUtf32(0x1F600);
        // The pair sits across the cut (units MaxExcerpt-2 and MaxExcerpt-1): it goes whole, never half.
        var excerpt = PostService.ImportedExcerpt(new string('x', PostService.MaxExcerpt - 2) + smile + "y");
        Assert.Equal(new string('x', PostService.MaxExcerpt - 2) + "…", excerpt);
        Assert.Null(XmlText.FirstUncarryable(excerpt));
        Assert.True(excerpt.Length <= PostService.MaxExcerpt);
        // One that fits is left as it is.
        var fits = new string('x', PostService.MaxExcerpt - 2) + smile;
        Assert.Equal(fits, PostService.ImportedExcerpt(fits));
    }
}
