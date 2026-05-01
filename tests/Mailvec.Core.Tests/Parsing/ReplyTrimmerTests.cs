using Mailvec.Core.Parsing;

namespace Mailvec.Core.Tests.Parsing;

public class ReplyTrimmerTests
{
    [Fact]
    public void Cuts_at_gmail_style_reply_header()
    {
        var body = """
            Thanks Nancy!

            Added to the website.

            Dan

            On Tue, Jan 27, 2026 at 2:46 PM Nancy Aller <nancyaller@hotmail.com> wrote:

            Minutes from the January 20, 2026 Board Meeting

            Held at Nancy Aller's house
            """;

        var result = ReplyTrimmer.Trim(body, subject: "Re: HME Board minutes from January");

        result.ShouldContain("Thanks Nancy!");
        result.ShouldContain("Added to the website.");
        result.ShouldNotContain("On Tue, Jan 27");
        result.ShouldNotContain("Minutes from the January");
    }

    [Fact]
    public void Cuts_at_outlook_style_header_when_subject_is_a_reply()
    {
        var body = """
            Bill Hylen lives at 1566 Tanglewood, between Frank Farnesi and the McLaughlins.

            From: Dan Delaney <dan@hactar.com>

            Sent: Tuesday, February 3, 2026 9:45 AM

            To: Nancy Aller <nancyaller@hotmail.com>

            Subject: Fwd: PLEASE RUSH** 1100211633

            Original quoted content lives down here.
            """;

        var result = ReplyTrimmer.Trim(body, subject: "Re: PLEASE RUSH** 1100211633 HYLEN, WILLIAM 24 MONTH LEDGER");

        result.ShouldContain("Bill Hylen lives at 1566 Tanglewood");
        result.ShouldNotContain("From: Dan Delaney");
        result.ShouldNotContain("Original quoted content");
    }

    [Fact]
    public void Leaves_outlook_header_alone_for_forwarded_messages()
    {
        // For a forward, the "From: ..." block introduces the meaningful
        // payload. Stripping at that line would lose the actual content.
        var body = """
            FYI — see below.

            From: Stephen Evans <evans@example.com>

            Sent: Monday, January 5, 2026 10:00 AM

            Subject: Quarterly budget update

            The board approved the budget with these changes...
            """;

        var result = ReplyTrimmer.Trim(body, subject: "Fwd: Quarterly budget update");

        result.ShouldContain("FYI");
        result.ShouldContain("From: Stephen Evans");
        result.ShouldContain("The board approved the budget");
    }

    [Fact]
    public void Strips_rfc3676_quoted_lines_individually()
    {
        var body = """
            Reply text here.

            > This was the original message
            > spread across multiple lines
            > that we don't want indexed twice.

            More reply text.
            """;

        var result = ReplyTrimmer.Trim(body, subject: "Re: something");

        result.ShouldContain("Reply text here.");
        result.ShouldContain("More reply text.");
        result.ShouldNotContain("This was the original message");
        result.ShouldNotContain("spread across multiple lines");
    }

    [Fact]
    public void Cuts_at_outlook_original_message_separator()
    {
        var body = """
            My response.

            -----Original Message-----
            From: someone@example.com
            Subject: Original

            Original message body that should be dropped.
            """;

        var result = ReplyTrimmer.Trim(body, subject: "Re: Original");

        result.ShouldContain("My response.");
        result.ShouldNotContain("Original message body");
    }

    [Fact]
    public void Returns_unchanged_when_no_reply_marker_present()
    {
        var body = """
            Hi there,

            This is a brand-new message with no reply context.
            Just regular content.

            Best,
            Dan
            """;

        var result = ReplyTrimmer.Trim(body, subject: "New thing");

        result.ShouldContain("This is a brand-new message");
        result.ShouldContain("Best,\nDan");
    }

    [Fact]
    public void Tolerates_empty_or_null_body()
    {
        ReplyTrimmer.Trim(string.Empty, subject: "Re: anything").ShouldBe(string.Empty);
    }

    [Fact]
    public void Does_not_strip_outlook_block_when_subject_is_null()
    {
        // Without a Re:/RE: signal we can't tell reply from forward, so we
        // leave the Outlook-style block alone. Gmail "On X wrote:" still
        // applies because that pattern is unambiguous.
        var body = """
            Some content.

            From: a@x

            Sent: today

            Subject: hello
            """;

        var result = ReplyTrimmer.Trim(body, subject: null);

        result.ShouldContain("From: a@x");
    }

    [Fact]
    public void Cuts_gmail_reply_header_regardless_of_subject_prefix()
    {
        // "On X wrote:" is a reliable enough reply marker that we cut on it
        // even if the subject doesn't have Re: (some clients re-thread weirdly).
        var body = """
            New text up here.

            On Mon, Mar 3, 2025 at 9:00 AM someone@example.com wrote:

            Old body.
            """;

        var result = ReplyTrimmer.Trim(body, subject: "Some unrelated subject");

        result.ShouldContain("New text up here");
        result.ShouldNotContain("Old body");
    }
}
