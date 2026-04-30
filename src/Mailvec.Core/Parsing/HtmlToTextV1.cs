using System.Text;
using MimeKit.Text;

namespace Mailvec.Core.Parsing;

/// <summary>
/// Original HTML-to-text conversion: walks MimeKit's HtmlTokenizer token stream
/// and emits visible text with newlines on common block-level tags. Cheap and
/// dependency-free, but lets marketing-email noise (hidden preheader text,
/// tracking pixels, raw href URLs) through. Kept for side-by-side comparison
/// against <see cref="HtmlToTextV2"/>; production callers should pick one.
/// </summary>
public static class HtmlToTextV1
{
    public static string Convert(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var tokenizer = new HtmlTokenizer(new StringReader(html));
        var sb = new StringBuilder(html.Length);
        bool insideScript = false;
        bool insideStyle = false;

        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token.Kind)
            {
                case HtmlTokenKind.Tag:
                    var tag = (HtmlTagToken)token;
                    var name = tag.Name?.ToLowerInvariant();
                    if (name == "script") insideScript = !tag.IsEndTag && !tag.IsEmptyElement;
                    else if (name == "style") insideStyle = !tag.IsEndTag && !tag.IsEmptyElement;
                    else if (name is "p" or "br" or "div" or "li" or "tr" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6") sb.Append('\n');
                    break;
                case HtmlTokenKind.Data when !insideScript && !insideStyle:
                    sb.Append(((HtmlDataToken)token).Data);
                    break;
            }
        }

        return TextNormalize.Apply(sb.ToString());
    }
}
