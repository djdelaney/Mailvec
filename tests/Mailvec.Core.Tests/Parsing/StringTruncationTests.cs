using Mailvec.Core.Parsing;

namespace Mailvec.Core.Tests.Parsing;

public class StringTruncationTests
{
    [Fact]
    public void Short_strings_pass_through_unchanged()
    {
        StringTruncation.Truncate("hello", 240).ShouldBe("hello");
    }

    [Fact]
    public void Long_strings_are_cut_with_ellipsis()
    {
        var s = new string('a', 300);
        var t = StringTruncation.Truncate(s, 240);
        t.Length.ShouldBe(241);           // 240 + ellipsis
        t.ShouldEndWith("…");
    }

    [Fact]
    public void Cut_landing_on_a_surrogate_pair_backs_off_one_unit()
    {
        // "🎉" is a surrogate pair (2 UTF-16 units). Place it so the naive
        // cut lands between its halves: 239 'a's then the pair — s[..240]
        // would keep only the high surrogate, which JSON serializes as U+FFFD.
        var s = new string('a', 239) + "🎉" + new string('b', 50);

        var t = StringTruncation.Truncate(s, 240);

        t.ShouldEndWith("…");
        char.IsHighSurrogate(t[^2]).ShouldBeFalse();  // no lone surrogate before the ellipsis
        t[..^1].ShouldBe(new string('a', 239));       // pair dropped whole, not halved
    }

    [Fact]
    public void Cut_not_touching_a_pair_keeps_full_width()
    {
        var s = new string('a', 240) + "🎉";
        var t = StringTruncation.Truncate(s, 240);
        t[..^1].Length.ShouldBe(240);
    }
}
