using Mailvec.Core.Options;
using Mailvec.Core.Search;

namespace Mailvec.Core.Tests.Search;

public class WebmailLinkBuilderTests
{
    [Fact]
    public void Returns_null_when_message_id_is_null()
    {
        WebmailLinkBuilder.Build(null, new FastmailOptions { AccountId = "u12345678" })
            .ShouldBeNull();
    }

    [Fact]
    public void Returns_null_when_message_id_is_whitespace()
    {
        WebmailLinkBuilder.Build("   ", new FastmailOptions { AccountId = "u12345678" })
            .ShouldBeNull();
    }

    [Fact]
    public void Returns_null_when_account_id_is_empty()
    {
        WebmailLinkBuilder.Build("<abc@example.com>", new FastmailOptions { AccountId = "" })
            .ShouldBeNull();
    }

    [Fact]
    public void Returns_null_when_account_id_is_whitespace()
    {
        WebmailLinkBuilder.Build("<abc@example.com>", new FastmailOptions { AccountId = "   " })
            .ShouldBeNull();
    }

    [Fact]
    public void Builds_link_with_default_web_url()
    {
        var url = WebmailLinkBuilder.Build("<abc@example.com>", new FastmailOptions { AccountId = "u12345678" });
        url.ShouldBe("https://app.fastmail.com/mail/search:msgid:%3Cabc%40example.com%3E?u=u12345678");
    }

    [Fact]
    public void Url_escapes_message_id_special_chars()
    {
        // '+' must be percent-encoded so Fastmail's URL parser doesn't read it as a space.
        var url = WebmailLinkBuilder.Build("<a+b/c@host>", new FastmailOptions { AccountId = "u1" });
        url.ShouldNotBeNull();
        url.ShouldContain("%2B");
        url.ShouldContain("%2F");
        url.ShouldNotContain("+");
    }

    [Fact]
    public void Url_escapes_account_id()
    {
        var url = WebmailLinkBuilder.Build("<x@y>", new FastmailOptions { AccountId = "u 12" });
        url!.ShouldEndWith("?u=u%2012");
    }

    [Fact]
    public void Custom_web_url_strips_trailing_slash()
    {
        var url = WebmailLinkBuilder.Build(
            "<x@y>",
            new FastmailOptions { AccountId = "u1", WebUrl = "https://mail.example.com/" });
        url.ShouldBe("https://mail.example.com/mail/search:msgid:%3Cx%40y%3E?u=u1");
    }

    [Fact]
    public void Null_web_url_falls_back_to_default()
    {
        var url = WebmailLinkBuilder.Build(
            "<x@y>",
            new FastmailOptions { AccountId = "u1", WebUrl = null! });
        url.ShouldStartWith("https://app.fastmail.com/mail/search:msgid:");
    }

    [Fact]
    public void MarkdownLink_returns_null_when_url_is_null()
    {
        WebmailLinkBuilder.MarkdownLink(null, "Anything").ShouldBeNull();
    }

    [Fact]
    public void MarkdownLink_wraps_a_plain_subject()
    {
        WebmailLinkBuilder.MarkdownLink("https://mail/x", "Q3 numbers")
            .ShouldBe("[Q3 numbers](https://mail/x)");
    }

    [Fact]
    public void MarkdownLink_falls_back_when_subject_is_missing()
    {
        WebmailLinkBuilder.MarkdownLink("https://mail/x", null)
            .ShouldBe("[(no subject)](https://mail/x)");
        WebmailLinkBuilder.MarkdownLink("https://mail/x", "   ")
            .ShouldBe("[(no subject)](https://mail/x)");
    }

    [Fact]
    public void MarkdownLink_escapes_a_subject_that_tries_to_spoof_the_target()
    {
        // A crafted subject that, unescaped, would render as a link to evil.com
        // with benign-looking text. Escaping the brackets keeps the whole subject
        // inside the link text so the real target survives.
        var link = WebmailLinkBuilder.MarkdownLink("https://mail/real", "Invoice](https://evil.com) [x");

        link.ShouldBe("[Invoice\\](https://evil.com) \\[x](https://mail/real)");
        // The real destination is the only (...) target the renderer will bind.
        link.ShouldEndWith("](https://mail/real)");
        // The evil closing-bracket is escaped (\]), so the renderer keeps it as
        // literal link text instead of ending the span and binding evil.com.
        link!.ShouldContain("\\](https://evil.com)");
    }

    [Fact]
    public void MarkdownLink_escapes_backslashes_before_brackets()
    {
        // A trailing backslash must not escape our closing ']' — escape '\' first.
        WebmailLinkBuilder.MarkdownLink("https://mail/x", "weird\\")
            .ShouldBe("[weird\\\\](https://mail/x)");
    }

    [Fact]
    public void MarkdownLink_collapses_newlines_in_the_subject()
    {
        WebmailLinkBuilder.MarkdownLink("https://mail/x", "line one\r\nline two")
            .ShouldBe("[line one  line two](https://mail/x)");
    }

    [Theory]
    // An angle-bracket autolink binds tighter than link brackets: unescaped,
    // the renderer shows the sender's URL as the live link and ours as text.
    [InlineData("Invoice <https://evil.example/pay>", "[Invoice \\<https://evil.example/pay\\>](https://mail/x)")]
    [InlineData("<a href=\"https://evil.example\">Open</a>", "[\\<a href=\"https://evil.example\"\\>Open\\</a\\>](https://mail/x)")]
    [InlineData("<pay@evil.example>", "[\\<pay@evil.example\\>](https://mail/x)")]
    // So does a code span, which would swallow our closing bracket.
    [InlineData("a `b", "[a \\`b](https://mail/x)")]
    public void MarkdownLink_escapes_constructs_that_bind_tighter_than_the_link(string subject, string expected)
    {
        WebmailLinkBuilder.MarkdownLink("https://mail/x", subject).ShouldBe(expected);
    }

    [Fact]
    public void MarkdownLink_leaves_no_unescaped_structural_character_in_the_text()
    {
        var link = WebmailLinkBuilder.MarkdownLink("https://mail/x", "\\<[`]>\\\\ <<x>> ``y``")!;
        var text = link[1..^"](https://mail/x)".Length];

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }   // an escape consumes the next character
            text[i].ShouldNotBeOneOf('[', ']', '<', '>', '`');
        }
    }

    [Theory]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\u0085b")]
    [InlineData("a\tb")]
    [InlineData("a\u000Bb")]
    public void MarkdownLink_turns_every_line_break_and_control_character_into_a_space(string subject)
    {
        WebmailLinkBuilder.MarkdownLink("https://mail/x", subject).ShouldBe("[a b](https://mail/x)");
    }
}
