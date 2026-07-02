using Mailvec.Core.Parsing;

namespace Mailvec.Core.Tests.Parsing;

/// <summary>
/// Hostile / pathological HTML must never take down the process. A
/// StackOverflowException is uncatchable, so a single malformed message with
/// tens of thousands of nested tags would kill the indexer — and because the
/// initial scan re-parses the same file after the launchd restart, it becomes
/// a deterministic crash loop. These tests fail as a dead test-host (not a
/// normal assertion failure) if the depth cap regresses.
/// </summary>
public class HtmlToTextHostileInputTests
{
    [Fact]
    public void Deeply_nested_html_does_not_overflow_the_stack()
    {
        // ~50k nested divs (250KB) — far past any real email, well past the
        // depth that used to overflow the recursion.
        var html = "<p>shallow content survives</p>" + string.Concat(Enumerable.Repeat("<div>", 50_000))
                 + "buried" + string.Concat(Enumerable.Repeat("</div>", 50_000));

        var text = HtmlToText.Convert(html);

        text.ShouldContain("shallow content survives");
    }

    [Fact]
    public void Deeply_nested_subtree_inside_a_link_does_not_overflow_the_stack()
    {
        // The <a> branch checks the subtree for visible text; that check must
        // be iterative too, or a hostile-depth subtree under a link blows the
        // stack through the traversal even with the Walk depth cap in place.
        var html = "<a href=\"https://example.com\">" + string.Concat(Enumerable.Repeat("<i>", 50_000))
                 + "deep link text" + string.Concat(Enumerable.Repeat("</i>", 50_000)) + "</a><p>after</p>";

        var text = HtmlToText.Convert(html);

        text.ShouldContain("after");
    }

    [Fact]
    public void Content_within_the_depth_cap_is_preserved()
    {
        // 100 levels of nesting is Outlook-table-soup territory and must
        // convert fully.
        var html = string.Concat(Enumerable.Repeat("<div>", 100))
                 + "legitimate deep content" + string.Concat(Enumerable.Repeat("</div>", 100));

        HtmlToText.Convert(html).ShouldContain("legitimate deep content");
    }

    [Fact]
    public void Oversized_html_input_is_truncated_not_parsed_unbounded()
    {
        // 4MB of markup — the converter caps input at 1MB before parsing.
        // Early content must survive; the call must return promptly.
        var filler = string.Concat(Enumerable.Repeat("<p>x</p>", 500_000)); // ~4MB
        var html = "<p>early marker text</p>" + filler;

        var text = HtmlToText.Convert(html);

        text.ShouldContain("early marker text");
    }
}
