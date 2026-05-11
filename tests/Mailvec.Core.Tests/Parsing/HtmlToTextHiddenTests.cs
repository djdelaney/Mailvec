using Mailvec.Core.Parsing;

namespace Mailvec.Core.Tests.Parsing;

/// <summary>
/// Branch coverage for <c>HtmlToText.IsHidden</c> /
/// <c>IsTrackingPixel</c> / <c>IsBoilerplateLink</c> — the marketing-email
/// noise filters that the boilerplate-pattern tests don't exercise. All
/// inputs go through the public <see cref="HtmlToText.Convert"/> entry point.
/// </summary>
public sealed class HtmlToTextHiddenTests
{
    [Theory]
    [InlineData("display:none")]
    [InlineData("display: none")]
    [InlineData("visibility:hidden")]
    [InlineData("visibility: hidden")]
    [InlineData("opacity:0")]
    [InlineData("opacity: 0")]
    [InlineData("opacity: 0;")]
    [InlineData("mso-hide:all")]
    [InlineData("mso-hide: all")]
    public void Inline_style_hides_preheader_text(string style)
    {
        var html = $"<div style=\"{style}\">Hidden preheader</div><p>Visible body</p>";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("Visible body");
        text.ShouldNotContain("Hidden preheader");
    }

    [Fact]
    public void Hidden_attribute_hides_element()
    {
        var html = "<div hidden>Secret</div><p>Visible</p>";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("Visible");
        text.ShouldNotContain("Secret");
    }

    [Fact]
    public void Aria_hidden_true_hides_element()
    {
        var html = "<div aria-hidden=\"true\">Decorative</div><p>Content</p>";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("Content");
        text.ShouldNotContain("Decorative");
    }

    [Fact]
    public void Font_size_zero_is_NOT_treated_as_hidden()
    {
        // Layout-hack guard: marketing emails wrap <td> in font-size:0 to
        // defeat inter-cell whitespace, then override inside. Treating it as
        // hidden would nuke legitimate inner content.
        var html = "<td style=\"font-size:0\"><span style=\"font-size:14px\">Real body copy</span></td>";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("Real body copy");
    }

    [Fact]
    public void Max_height_zero_is_NOT_treated_as_hidden()
    {
        var html = "<td style=\"max-height:0\"><span>Important content</span></td>";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("Important content");
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("0", "0")]
    [InlineData("1px", "1px")]
    [InlineData("1PX", "1PX")]
    public void Tiny_dimension_img_treated_as_tracking_pixel(string w, string h)
    {
        var html = $"<p>Body</p><img src=\"https://t.example/x\" width=\"{w}\" height=\"{h}\" alt=\"pixel-alt\">";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("Body");
        text.ShouldNotContain("pixel-alt");
    }

    [Fact]
    public void Img_with_inline_1px_style_treated_as_tracking_pixel()
    {
        var html = """<p>Body</p><img src="https://t.example/x" style="width:1px;height:1px" alt="px-alt">""";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("Body");
        text.ShouldNotContain("px-alt");
    }

    [Fact]
    public void Img_with_normal_dimensions_is_not_stripped_as_tracking_pixel()
    {
        // Img alt is not emitted by the walker today regardless, but the
        // element itself shouldn't be classified as a tracking pixel and
        // skipped — its non-img siblings should still walk normally.
        var html = """<p>Before</p><img src="https://e/x" width="600" height="200" alt="banner"><p>After</p>""";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("Before");
        text.ShouldContain("After");
    }

    [Theory]
    [InlineData("https://list.example.com/unsubscribe?u=123")]
    [InlineData("https://list.example.com/optout")]
    [InlineData("https://list.example.com/opt-out/x")]
    [InlineData("https://list.example.com/opt_out/x")]
    [InlineData("https://list.example.com/manage_preferences")]
    [InlineData("https://list.example.com/manage-preferences")]
    [InlineData("https://list.example.com/email_preferences")]
    [InlineData("https://list.example.com/email-preferences")]
    [InlineData("https://list.example.com/emailpreferences")]
    [InlineData("https://list.example.com/pref_center")]
    [InlineData("https://list.example.com/preference-center")]
    [InlineData("https://list.example.com/manage-subscriptions")]
    [InlineData("https://list.example.com/update_preferences")]
    [InlineData("https://list.example.com/list-unsubscribe/x")]
    public void Boilerplate_link_text_is_dropped(string href)
    {
        var html = $"<p>Body copy</p><a href=\"{href}\">Manage preferences</a>";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("Body copy");
        text.ShouldNotContain("Manage preferences");
    }

    [Fact]
    public void Non_boilerplate_link_keeps_its_text_drops_its_href()
    {
        var html = """<p>Read the <a href="https://blog.example.com/post/42">deep-dive</a> here.</p>""";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("deep-dive");
        text.ShouldNotContain("blog.example.com");
    }

    [Fact]
    public void Image_only_anchor_is_dropped()
    {
        // <a> wrapping only an <img> (typical for social icons) has no
        // visible text — drop the whole anchor.
        var html = """<p>Body</p><a href="https://twitter.com/x"><img src="https://e/icon.png"></a>""";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("Body");
        text.ShouldNotContain("https://twitter.com");
    }

    [Fact]
    public void Br_emits_newline()
    {
        var html = "<p>line one<br>line two</p>";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("line one");
        text.ShouldContain("line two");
        var oneIdx = text.IndexOf("line one", StringComparison.Ordinal);
        var twoIdx = text.IndexOf("line two", StringComparison.Ordinal);
        text.Substring(oneIdx, twoIdx - oneIdx).ShouldContain("\n");
    }

    [Fact]
    public void Li_emits_bullet_prefix()
    {
        var html = "<ul><li>first item</li><li>second item</li></ul>";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("- first item");
        text.ShouldContain("- second item");
    }

    [Fact]
    public void Script_and_style_blocks_are_dropped()
    {
        var html = """
            <html><head><style>.x{color:red}</style></head>
            <body><script>alert(1)</script><p>Real content</p></body></html>
            """;

        var text = HtmlToText.Convert(html);

        text.ShouldContain("Real content");
        text.ShouldNotContain("alert");
        text.ShouldNotContain("color:red");
    }

    [Fact]
    public void Footer_and_address_blocks_are_dropped()
    {
        var html = """
            <p>Body of the email</p>
            <address>123 Main St, Anywhere USA</address>
            <footer>© 2026 Example Inc.</footer>
            """;

        var text = HtmlToText.Convert(html);

        text.ShouldContain("Body of the email");
        text.ShouldNotContain("123 Main St");
        text.ShouldNotContain("Example Inc");
    }

    [Fact]
    public void Empty_input_returns_empty_string()
    {
        Assert.Equal(string.Empty, HtmlToText.Convert(string.Empty));
    }

    [Fact]
    public void Document_without_body_walks_root_element()
    {
        // Bare-fragment input — AngleSharp still synthesises a <body>, so
        // the body-walk path covers this. Belt-and-braces: confirm no crash.
        var text = HtmlToText.Convert("<span>Just text</span>");
        text.ShouldContain("Just text");
    }
}
