using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Mailvec.Core.Parsing;

/// <summary>
/// HTML-to-text conversion using AngleSharp's DOM. Strips marketing-email
/// noise: hidden preheader text (display:none / visibility:hidden /
/// opacity:0 / mso-hide:all), tracking pixels, image-only and
/// unsubscribe/preferences links, and &lt;script&gt;/&lt;style&gt;/&lt;head&gt;/
/// &lt;address&gt;/&lt;footer&gt; blocks. Output runs through
/// <see cref="TextNormalize"/> (zero-width / NBSP cleanup, blank-line collapse)
/// and <see cref="BoilerplateFilter"/> (line-level footer phrases). Plain
/// text suitable for FTS indexing and embedding.
/// </summary>
public static class HtmlToText
{
    private static readonly HtmlParser Parser = new();

    private static readonly HashSet<string> DropTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "meta", "link", "title", "noscript", "template",
        // <address> is the legal-compliance physical-address block in many
        // marketing emails (CAN-SPAM). <footer> is the HTML5 semantic footer,
        // typically copyright / unsubscribe / preferences chrome.
        "address", "footer",
        // <blockquote> is intentionally NOT dropped: while Gmail and Apple Mail
        // use it to wrap quoted reply text, Outlook web uses it for content
        // layout (each paragraph of an original message gets wrapped in a
        // blockquote with class="elementToProof"). Dropping wholesale destroyed
        // legitimate Outlook content. Reply-text removal is handled at the
        // text level by ReplyTrimmer (catches "On X wrote:" headers regardless
        // of whether the surrounding quote was in a blockquote or not).
    };

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6",
        "section", "article", "header", "nav", "aside",
        "blockquote", "pre", "hr", "figure", "figcaption",
    };

    public static string Convert(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var doc = Parser.ParseDocument(html);
        var sb = new StringBuilder(html.Length);
        if (doc.Body is { } body)
        {
            Walk(body, sb);
        }
        else
        {
            Walk(doc.DocumentElement, sb);
        }
        var normalized = TextNormalize.Apply(sb.ToString());
        return BoilerplateFilter.Apply(normalized);
    }

    private static void Walk(INode node, StringBuilder sb)
    {
        if (node is IElement el)
        {
            var tag = el.LocalName;

            if (DropTags.Contains(tag)) return;
            if (IsHidden(el)) return;
            if (IsTrackingPixel(el)) return;

            if (string.Equals(tag, "a", StringComparison.OrdinalIgnoreCase))
            {
                // Drop unsubscribe / preferences / opt-out links entirely
                // (link text is boilerplate noise: "Manage preferences", etc).
                if (IsBoilerplateLink(el.GetAttribute("href"))) return;

                // Image-only links (no visible text) are usually social icons,
                // sponsor logos, or "tap to view" wrappers — drop them.
                if (string.IsNullOrWhiteSpace(el.TextContent)) return;

                // Otherwise keep the visible text and drop the href. Tracking
                // URLs are noise; link text carries the meaning ("Click here
                // to confirm" vs the actual URL).
                AppendChildren(el, sb);
                return;
            }

            if (string.Equals(tag, "li", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append('\n').Append("- ");
                AppendChildren(el, sb);
                return;
            }

            if (string.Equals(tag, "br", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append('\n');
                return;
            }

            if (BlockTags.Contains(tag))
            {
                sb.Append('\n');
                AppendChildren(el, sb);
                sb.Append('\n');
                return;
            }

            if (string.Equals(tag, "td", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "th", StringComparison.OrdinalIgnoreCase))
            {
                AppendChildren(el, sb);
                sb.Append(' ');
                return;
            }

            AppendChildren(el, sb);
            return;
        }

        if (node is IText t)
        {
            sb.Append(t.Data);
        }
    }

    private static void AppendChildren(IElement el, StringBuilder sb)
    {
        foreach (var child in el.ChildNodes)
        {
            Walk(child, sb);
        }
    }

    private static bool IsHidden(IElement el)
    {
        if (string.Equals(el.GetAttribute("hidden"), "hidden", StringComparison.OrdinalIgnoreCase) ||
            el.HasAttribute("hidden"))
        {
            return true;
        }
        if (string.Equals(el.GetAttribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var style = el.GetAttribute("style");
        if (string.IsNullOrEmpty(style)) return false;

        // Inline-style heuristics. Marketing emails embed preheader/preview
        // text in elements styled with display:none / visibility:hidden /
        // opacity:0 / mso-hide:all. We deliberately do NOT treat font-size:0,
        // line-height:0, max-height:0, or max-width:0 as hide signals: those
        // are common layout hacks on outer <td>/<table> wrappers (e.g. to
        // defeat inter-cell whitespace) where the real text lives in inner
        // elements that override the property. Treating them as hidden
        // nukes the entire branch and loses the visible content.
        var s = style.ToLowerInvariant();
        if (s.Contains("display:none") || s.Contains("display: none")) return true;
        if (s.Contains("visibility:hidden") || s.Contains("visibility: hidden")) return true;
        if (HasZero(s, "opacity")) return true;
        if (s.Contains("mso-hide:all") || s.Contains("mso-hide: all")) return true;
        return false;
    }

    private static bool HasZero(string style, string property)
    {
        var pattern = property + @"\s*:\s*0(\s*(px|pt|em|%|vh|vw)?\s*)?(;|$|\s)";
        return Regex.IsMatch(style, pattern);
    }

    private static bool IsTrackingPixel(IElement el)
    {
        if (el is not IHtmlImageElement img) return false;
        var w = img.GetAttribute("width");
        var h = img.GetAttribute("height");
        if (IsTinyDimension(w) && IsTinyDimension(h)) return true;

        var style = img.GetAttribute("style");
        if (!string.IsNullOrEmpty(style))
        {
            var s = style.ToLowerInvariant();
            if ((s.Contains("width:1px") || s.Contains("width: 1px")) &&
                (s.Contains("height:1px") || s.Contains("height: 1px")))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsTinyDimension(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var v = value.Trim().TrimEnd('p', 'x', 'P', 'X').Trim();
        return v == "0" || v == "1";
    }

    private static bool IsBoilerplateLink(string? href)
    {
        if (string.IsNullOrEmpty(href)) return false;
        var h = href.ToLowerInvariant();
        // Substring match against URL — robust across senders. Patterns are
        // specific enough not to false-positive on legitimate domains (the
        // bare word "preferences" would match far too much).
        return h.Contains("unsubscribe")
            || h.Contains("/optout") || h.Contains("/opt-out") || h.Contains("/opt_out")
            || h.Contains("manage_preferences") || h.Contains("manage-preferences")
            || h.Contains("email_preferences") || h.Contains("email-preferences")
            || h.Contains("emailpreferences")
            || h.Contains("pref_center") || h.Contains("preference-center") || h.Contains("preference_center")
            || h.Contains("manage-subscriptions") || h.Contains("manage_subscriptions")
            || h.Contains("update-preferences") || h.Contains("update_preferences")
            || h.Contains("list-unsubscribe");
    }
}
