using System.Text;
using Mailvec.Core.Attachments;

namespace Mailvec.Core.Tests.Attachments;

public class RawHeaderBlockTests
{
    private static RawHeaderBlock Cut(string s, bool reachedEnd = true) =>
        RawHeaderBlock.FromBytes(Encoding.UTF8.GetBytes(s), reachedEnd);

    [Fact]
    public void A_message_that_opens_with_a_blank_line_has_no_headers()
    {
        Cut("\r\nbody\r\n").ShouldBe(new RawHeaderBlock("", Truncated: false));
        Cut("\nbody\n").ShouldBe(new RawHeaderBlock("", Truncated: false));
    }

    [Fact]
    public void A_line_containing_only_whitespace_is_a_folded_continuation_not_the_end()
    {
        // RFC 5322: only an EMPTY line ends the block.
        Cut("A: 1\r\n \r\nB: 2\r\n\r\nbody").Text.ShouldBe("A: 1\r\n \r\nB: 2\r\n");
    }

    [Fact]
    public void A_read_cut_mid_line_or_on_a_lone_CR_drops_the_partial_line()
    {
        Cut("A: 1\r\nB: par", reachedEnd: false).ShouldBe(new RawHeaderBlock("A: 1\r\n", Truncated: true));
        // The CR might be the start of the terminating CRLF or of nothing; the
        // read can't tell, so it is treated as unfinished.
        Cut("A: 1\r\n\r", reachedEnd: false).ShouldBe(new RawHeaderBlock("A: 1\r\n", Truncated: true));
    }

    [Fact]
    public void Invalid_utf8_is_replaced_not_fatal()
    {
        var bytes = new byte[] { (byte)'X', (byte)':', (byte)' ', 0xFF, (byte)'\n', (byte)'\n' };
        RawHeaderBlock.FromBytes(bytes, reachedEnd: true).Text.ShouldBe("X: �\n");
    }

    [Fact]
    public void Fields_attach_continuations_and_tolerate_malformed_lines()
    {
        var fields = new RawHeaderBlock("\tleading continuation\nA: 1\n  more\nnot a header\nB:2\n", false).Fields();

        fields.Select(f => f.Name).ShouldBe(["", "A", "", "B"]);
        fields[1].Raw.ShouldBe("A: 1\n  more\n");
        string.Concat(fields.Select(f => f.Raw)).ShouldBe("\tleading continuation\nA: 1\n  more\nnot a header\nB:2\n");
    }

    [Theory]
    [InlineData("Authentication-Results", "authentication-results", true)]
    [InlineData("ARC-Seal", "ARC-*", true)]
    [InlineData("ARC-Seal", " arc-* ", true)]
    [InlineData("Received", "Received:", true)]
    [InlineData("Received-SPF", "Received", false)]
    [InlineData("", "*", false)]
    public void Matches(string field, string pattern, bool expected) =>
        RawHeaderBlock.Matches(field, [pattern]).ShouldBe(expected);
}
