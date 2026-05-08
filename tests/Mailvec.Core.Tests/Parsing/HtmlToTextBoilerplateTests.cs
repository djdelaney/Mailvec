using Mailvec.Core.Parsing;

namespace Mailvec.Core.Tests.Parsing;

/// <summary>
/// End-to-end coverage of the boilerplate-stripping pass that runs as the
/// final stage of <see cref="HtmlToText.Convert"/>. We test through the
/// public API (rather than against the internal <c>BoilerplateFilter</c>)
/// because the patterns target text shapes that emerge after AngleSharp's
/// DOM walk and TextNormalize — testing earlier would let through
/// markup-fragment artefacts that the real pipeline cleans up.
/// </summary>
public class HtmlToTextBoilerplateTests
{
    [Fact]
    public void Strips_apple_receipt_template_footer_lines()
    {
        var html = """
            <p>Receipt</p>
            <p>iCloud+ with 2 TB (Monthly)</p>
            <p>Renews May 20, 2026</p>
            <p>If you have any questions about your bill, contact us.</p>
            <p>You may contact Apple for a full refund within 15 days.</p>
            <p>Partial refunds are available where required by law.</p>
            <p>You can turn off renewal receipts to stop getting emails.</p>
            <p>Turn Off Renewal Receipt Emails</p>
            <p>Get Help with Subscriptions and Purchases</p>
            <p>Purchase History ›</p>
            <p>TM and © 2026 Apple Inc. One Apple Park Way</p>
            """;

        var text = HtmlToText.Convert(html);

        // Signal stays.
        text.ShouldContain("iCloud+ with 2 TB");
        text.ShouldContain("Renews May 20, 2026");

        // Boilerplate gone.
        text.ShouldNotContain("If you have any questions about your bill");
        text.ShouldNotContain("contact Apple for a full refund");
        text.ShouldNotContain("Partial refunds");
        text.ShouldNotContain("turn off renewal receipts");
        text.ShouldNotContain("Turn Off Renewal Receipt Emails");
        text.ShouldNotContain("Get Help with Subscriptions");
        text.ShouldNotContain("Purchase History ›");
        text.ShouldNotContain("TM and ©");
    }

    [Fact]
    public void Strips_bullet_separated_short_link_rows()
    {
        var html = "<p>Apple Account • Terms of Sale • Privacy Policy</p><p>Real subscription content here.</p>";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("Real subscription content here");
        text.ShouldNotContain("Apple Account");
        text.ShouldNotContain("Terms of Sale");
        text.ShouldNotContain("Privacy Policy");
    }

    [Fact]
    public void Strips_lines_with_template_engine_placeholders()
    {
        // Template-engine bugs: Apple/Stripe/etc. occasionally ship emails
        // with their literal `@@var@@` or `{{var}}` syntax intact. The
        // surrounding sentence is always boilerplate (the placeholder is
        // there because it'd be replaced by a "manage your settings" URL or
        // similar), so we drop the whole line.
        var html = "<p>If you have questions, visit @@supportUrl@@.</p><p>Real content stays.</p>";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("Real content stays");
        text.ShouldNotContain("@@supportUrl@@");
    }

    [Fact]
    public void Keeps_ordinary_sentences_that_happen_to_share_keywords_with_boilerplate_patterns()
    {
        // Defensive: phrases like "review your subscription" or "get help"
        // appear in real content too. Patterns are anchored to specific
        // line-starters; ordinary sentences shouldn't be eaten.
        var html = """
            <p>Please review your subscription preferences in the dashboard.</p>
            <p>Get help from the team channel.</p>
            <p>Ship the package to West Chester PA 19380.</p>
            """;

        var text = HtmlToText.Convert(html);

        text.ShouldContain("review your subscription preferences");
        text.ShouldContain("Get help from the team channel");
        text.ShouldContain("West Chester PA 19380");
    }

    [Fact]
    public void Keeps_navigation_lines_that_are_too_long_to_be_link_chrome()
    {
        // Trailing-arrow heuristic is capped at 60 chars: a real sentence
        // ending with the same glyph (rare but possible) shouldn't be eaten.
        var html = "<p>This is an extremely long, content-bearing sentence that the user actually wants in the search index ›</p>";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("content-bearing sentence");
    }

    [Fact]
    public void Strips_trailing_arrow_nav_lines()
    {
        var html = "<p>Order Total: $42</p><p>Report a Problem ›</p><p>View Your Account ›</p>";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("Order Total: $42");
        text.ShouldNotContain("Report a Problem");
        text.ShouldNotContain("View Your Account");
    }
}
