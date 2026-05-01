using System.Text;
using System.Text.RegularExpressions;

namespace Mailvec.Core.Parsing;

/// <summary>
/// Strip quoted reply content from email body_text so each message contributes
/// only its <i>new</i> text to FTS / embeddings. Threaded conversations
/// otherwise repeat the same content through every reply, which both bloats
/// the index and hurts BM25 ranking (replies that quote the original outrank
/// the original because the matching tokens appear twice).
///
/// Cuts at the first detected reply marker and discards everything below.
/// Forwarded-message markers are deliberately left alone because the
/// forwarded payload is the meaningful content of a forward.
/// </summary>
public static class ReplyTrimmer
{
    // "On Tue, Jan 27, 2026 at 2:46 PM Nancy Aller <nancyaller@hotmail.com> wrote:"
    // Apple Mail / Gmail / generic. Strict enough that this string almost never
    // appears legitimately outside a reply context.
    private static readonly Regex GmailReplyHeader = new(
        @"^\s*On .+ wrote:?\s*$",
        RegexOptions.Compiled);

    public static string Trim(string body, string? subject)
    {
        if (string.IsNullOrEmpty(body)) return body;

        var isReply = subject is not null
            && (subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                || subject.StartsWith("RE:", StringComparison.OrdinalIgnoreCase));

        var lines = body.Split('\n');
        var cutoffIdx = lines.Length;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmedLine = line.Trim();

            if (GmailReplyHeader.IsMatch(line))
            {
                cutoffIdx = i;
                break;
            }

            if (string.Equals(trimmedLine, "-----Original Message-----", StringComparison.OrdinalIgnoreCase))
            {
                cutoffIdx = i;
                break;
            }

            // Outlook-style header block (From/Sent/To/Subject) only counts as
            // a cutoff when the message is a reply (Re: subject). Forwards
            // reuse the same header layout and we want to keep the forwarded
            // content because that's the point of a forward.
            if (isReply && IsOutlookReplyHeaderStart(lines, i))
            {
                cutoffIdx = i;
                break;
            }
        }

        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < cutoffIdx; i++)
        {
            var line = lines[i];
            // Strip RFC-3676 / mutt-style ">"-prefixed quoted lines individually
            // (covers plaintext threads where the cutoff marker is missing or
            // the new text is interleaved with quoted excerpts).
            if (line.TrimStart().StartsWith('>')) continue;
            sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Heuristic: a line starting with "From: " is the start of an Outlook
    /// reply header block when at least two of "Sent:", "To:", "Subject:"
    /// appear within the next 6 lines (Outlook commonly inserts blank lines
    /// between header rows).
    /// </summary>
    private static bool IsOutlookReplyHeaderStart(string[] lines, int idx)
    {
        var line = lines[idx].TrimStart();
        if (!line.StartsWith("From: ", StringComparison.Ordinal)) return false;

        var hits = 0;
        var until = Math.Min(idx + 7, lines.Length);
        for (var j = idx + 1; j < until; j++)
        {
            var look = lines[j].TrimStart();
            if (look.StartsWith("Sent: ", StringComparison.Ordinal)) hits++;
            else if (look.StartsWith("To: ", StringComparison.Ordinal)) hits++;
            else if (look.StartsWith("Subject: ", StringComparison.Ordinal)) hits++;
        }
        return hits >= 2;
    }
}
