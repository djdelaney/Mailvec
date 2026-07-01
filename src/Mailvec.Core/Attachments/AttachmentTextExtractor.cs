using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Mailvec.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Mailvec.Core.Attachments;

/// <summary>
/// Recovers plain text from email attachments so it can be embedded for
/// semantic search. Currently handles PDF (PdfPig), DOCX (DocumentFormat.OpenXml),
/// iCalendar (.ics / VCALENDAR), vCard (.vcf / VCARD), and plain text (UTF-8 with
/// Windows-1252 fallback). Anything else returns
/// status='unsupported' with no text. The result is meant to feed the embedder,
/// not to be displayed to the user — light normalization (whitespace,
/// invisible chars) is good enough.
///
/// Failure modes are explicit so the indexer can stamp a terminal status and
/// not retry forever:
/// - 'done'        — text extracted successfully (may still be empty if the
///                   document is genuinely empty after stripping; the indexer
///                   uses 'no_text' for that case).
/// - 'no_text'     — the format was supported but produced no usable text at
///                   index time (e.g. scanned/image-only PDF). The embedder's
///                   OCR pass may later recover text and re-stamp it 'ocr'.
/// - 'ocr'         — text recovered by the embedder's vision-model OCR pass
///                   (scanned PDF). Treated as searchable like 'done'; written
///                   by MessageRepository.SaveOcrText, not the indexer.
/// - 'unsupported' — content type / extension not in our handler list.
/// - 'oversize'    — exceeds Indexer:AttachmentMaxBytes; not even attempted.
/// - 'encrypted'   — PDF / DOCX is password-protected.
/// - 'failed'      — parser threw on otherwise-supported input (corrupt file),
///                   or OCR could not open the PDF.
///
/// All stable status values; persisted in attachments.extraction_status.
/// </summary>
public sealed class AttachmentTextExtractor(
    IOptions<IndexerOptions> indexerOptions,
    ILogger<AttachmentTextExtractor> logger)
{
    public const string StatusDone = "done";
    public const string StatusNoText = "no_text";
    public const string StatusOcr = "ocr";
    public const string StatusUnsupported = "unsupported";
    public const string StatusOversize = "oversize";
    public const string StatusEncrypted = "encrypted";
    public const string StatusFailed = "failed";

    private const int MaxExtractedTextChars = 2_000_000;

    // .NET ships only UTF-*, ASCII, and Latin1 wired up by default — legacy
    // codepages like windows-1252 need CodePagesEncodingProvider registered
    // first (the type is in the shared framework on net10.0, no package needed),
    // or Encoding.GetEncoding("windows-1252") throws ArgumentException. Do it
    // once per process in the static ctor and cache the resolved encoding so the
    // per-attachment decode fallback (text/plain + calendar) never re-resolves.
    private static readonly Encoding Windows1252;

    static AttachmentTextExtractor()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Windows1252 = Encoding.GetEncoding("windows-1252");
    }

    private readonly long _maxBytes = indexerOptions.Value.AttachmentMaxBytes;

    public ExtractionResult Extract(MimeEntity entity, string? fileName, string? contentType, long? declaredSize)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var format = ResolveFormat(contentType, fileName);
        if (format == AttachmentFormat.Unsupported)
        {
            return new ExtractionResult(null, StatusUnsupported);
        }

        // Cheap prefilter — many oversized PDFs report size in the MIME header,
        // so we can skip decoding entirely. We still re-check after decode for
        // attachments that lie about Content-Length.
        if (declaredSize is { } sz && sz > _maxBytes)
        {
            logger.LogDebug("Skipping oversized attachment {Name}: {Size}b > {Max}b", fileName, sz, _maxBytes);
            return new ExtractionResult(null, StatusOversize);
        }

        byte[] bytes;
        try
        {
            bytes = DecodeEntity(entity);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to decode MIME entity for {Name}", fileName);
            return new ExtractionResult(null, StatusFailed);
        }

        if (bytes.LongLength > _maxBytes)
        {
            return new ExtractionResult(null, StatusOversize);
        }

        return format switch
        {
            AttachmentFormat.Pdf => ExtractPdf(bytes, fileName),
            AttachmentFormat.Docx => ExtractDocx(bytes, fileName),
            AttachmentFormat.Calendar => ExtractCalendar(bytes),
            AttachmentFormat.VCard => ExtractVCard(bytes),
            AttachmentFormat.Text => ExtractText(bytes),
            _ => new ExtractionResult(null, StatusUnsupported),
        };
    }

    private ExtractionResult ExtractPdf(byte[] bytes, string? fileName)
    {
        // Lenient parsing tolerates PDFs whose `startxref` is past the
        // default 2048-byte search window, dangling cross-reference entries,
        // and other common conformance bugs from older mailers / scanners.
        // The cost is mostly a few more bytes scanned on the recovery path.
        var options = new ParsingOptions { UseLenientParsing = true };

        try
        {
            using var doc = PdfDocument.Open(bytes, options);
            if (doc.IsEncrypted)
            {
                return new ExtractionResult(null, StatusEncrypted);
            }

            var sb = new StringBuilder(Math.Min(bytes.Length, 65_536));
            foreach (Page page in doc.GetPages())
            {
                if (sb.Length >= MaxExtractedTextChars) break;
                // ContentOrderTextExtractor produces output in PDF content-stream
                // order, which preserves multi-column reading order better than
                // the default top-down pass for PDFs that lay out side-by-side
                // text frames.
                var pageText = ContentOrderTextExtractor.GetText(page);
                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    sb.AppendLine(pageText);
                    sb.AppendLine();
                }
            }

            return BuildResult(sb.ToString());
        }
        catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException)
        {
            return new ExtractionResult(null, StatusEncrypted);
        }
        catch (PdfDocumentFormatException ex)
        {
            // Expected for non-conforming PDFs (mostly older corporate/legal
            // mailers and scanner output). One-liner — full stack trace is
            // noise since we already know the cause class. The attachment
            // gets stamped 'failed' and the embedder won't retry.
            logger.LogWarning("PDF text extraction failed for {Name}: {Message}", fileName, ex.Message);
            return new ExtractionResult(null, StatusFailed);
        }
        catch (Exception ex)
        {
            // Unexpected — keep the full stack so we can dig in.
            logger.LogWarning(ex, "PDF text extraction failed for {Name}", fileName);
            return new ExtractionResult(null, StatusFailed);
        }
    }

    private ExtractionResult ExtractDocx(byte[] bytes, string? fileName)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var doc = WordprocessingDocument.Open(ms, isEditable: false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null) return new ExtractionResult(null, StatusNoText);

            var sb = new StringBuilder(Math.Min(bytes.Length, 65_536));
            // Walk paragraphs explicitly so we keep paragraph breaks; the
            // default InnerText flattens everything onto one line, which
            // hurts the chunker's paragraph-aware splitting.
            foreach (var para in body.Descendants<Paragraph>())
            {
                if (sb.Length >= MaxExtractedTextChars) break;
                var text = para.InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine(text);
                }
            }

            return BuildResult(sb.ToString());
        }
        catch (FileFormatException ex) when (ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
        {
            return new ExtractionResult(null, StatusEncrypted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DOCX text extraction failed for {Name}", fileName);
            return new ExtractionResult(null, StatusFailed);
        }
    }

    private static ExtractionResult ExtractText(byte[] bytes)
    {
        // Strict UTF-8 first; many "text/plain" attachments are actually
        // Windows-1252 (legacy mailers, exported notes). Falling back to that
        // covers the vast majority of remaining cases without dragging in a
        // full charset detector.
        string? decoded = TryDecode(bytes, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        decoded ??= TryDecode(bytes, Windows1252);
        if (decoded is null) return new ExtractionResult(null, StatusFailed);
        return BuildResult(decoded);
    }

    /// <summary>
    /// Flattens an iCalendar (.ics / VCALENDAR) file into clean searchable text.
    /// ICS is line-based text, so the generic text path "works" — but RFC 5545
    /// line folding (a CRLF followed by a space continues the previous line)
    /// splits words and numbers across lines ("$225.00" becomes "$225.0\n 0"),
    /// and the raw form is dominated by machine noise (UID, DTSTAMP, SEQUENCE,
    /// TZID blocks). We unfold first, then keep only the human-meaningful
    /// properties and unescape RFC 5545 §3.3.11 text escapes, so search matches
    /// the summary / location / description / dates / participant names instead
    /// of protocol scaffolding.
    /// </summary>
    private static ExtractionResult ExtractCalendar(byte[] bytes)
    {
        // Same decode ladder as ExtractText — .ics is UTF-8 by spec but legacy
        // exporters (Outlook .vcs) still emit Windows-1252.
        string? decoded = TryDecode(bytes, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        decoded ??= TryDecode(bytes, Windows1252);
        if (decoded is null) return new ExtractionResult(null, StatusFailed);

        var sb = new StringBuilder(Math.Min(bytes.Length, 8_192));
        foreach (var line in UnfoldContentLines(decoded))
        {
            var (name, parameters, value) = SplitContentLine(line);
            switch (name)
            {
                case "SUMMARY":
                case "DESCRIPTION":
                case "COMMENT":
                    AppendField(sb, null, UnescapeStructuredText(value));
                    break;
                case "LOCATION":
                    AppendField(sb, "Location", UnescapeStructuredText(value));
                    break;
                case "DTSTART":
                    AppendField(sb, "When", value);
                    break;
                case "ORGANIZER":
                    AppendField(sb, "Organizer", ParamValue(parameters, "CN") ?? StripMailto(value));
                    break;
                case "ATTENDEE":
                    AppendField(sb, "Attendee", ParamValue(parameters, "CN") ?? StripMailto(value));
                    break;
            }
        }

        return BuildResult(sb.ToString());
    }

    private static void AppendField(StringBuilder sb, string? label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (label is not null) sb.Append(label).Append(": ");
        sb.AppendLine(value.Trim());
    }

    /// <summary>
    /// Flattens a vCard (.vcf / VCARD) into clean searchable text. Same story as
    /// <see cref="ExtractCalendar"/>: the raw text path "works" but leaks property
    /// names and — worse — an embedded PHOTO/LOGO property dumps a multi-kilobyte
    /// base64 blob into the search index. We unfold, then keep the human fields
    /// (name / org / title / email / phone / address / note / url) and drop the
    /// rest. FN is the display name; N is the structured fallback when FN is
    /// absent. ORG/N/ADR are ';'-delimited structured values, joined readably.
    /// </summary>
    private static ExtractionResult ExtractVCard(byte[] bytes)
    {
        string? decoded = TryDecode(bytes, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        decoded ??= TryDecode(bytes, Windows1252);
        if (decoded is null) return new ExtractionResult(null, StatusFailed);

        var sb = new StringBuilder(Math.Min(bytes.Length, 4_096));
        bool hasDisplayName = false;
        string? structuredName = null;

        foreach (var line in UnfoldVCardLines(decoded))
        {
            var (name, parameters, value) = SplitContentLine(line);
            // vCard 2.1 Outlook/hotel cards encode text properties quoted-
            // printable (=0D=0A etc.); decode before the field logic runs so the
            // real characters — not the =XX escapes — reach the search index.
            if (parameters.Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))
            {
                value = QpDecode(value);
            }
            switch (name)
            {
                case "FN":
                    AppendField(sb, null, UnescapeStructuredText(value));
                    hasDisplayName = true;
                    break;
                case "N":
                    // family;given;additional;prefix;suffix — space-join for a name.
                    structuredName = JoinStructured(value, " ");
                    break;
                case "NICKNAME":
                    AppendField(sb, "Nickname", UnescapeStructuredText(value));
                    break;
                case "ORG":
                    AppendField(sb, "Org", JoinStructured(value, " "));
                    break;
                case "TITLE":
                    AppendField(sb, "Title", UnescapeStructuredText(value));
                    break;
                case "EMAIL":
                    AppendField(sb, "Email", value.Trim());
                    break;
                case "TEL":
                    AppendField(sb, "Tel", value.Trim());
                    break;
                case "ADR":
                    // po-box;extended;street;locality;region;postal;country.
                    AppendField(sb, "Address", JoinStructured(value, ", "));
                    break;
                case "URL":
                    AppendField(sb, "URL", value.Trim());
                    break;
                case "NOTE":
                    AppendField(sb, null, UnescapeStructuredText(value));
                    break;
            }
        }

        if (!hasDisplayName && !string.IsNullOrWhiteSpace(structuredName))
        {
            AppendField(sb, null, structuredName);
        }

        return BuildResult(sb.ToString());
    }

    /// <summary>
    /// Split a structured vCard value on unescaped ';' component separators, then
    /// unescape and join the non-empty parts with <paramref name="separator"/>.
    /// Splitting before unescaping is what keeps a literal <c>\;</c> inside a
    /// component from being mistaken for a separator.
    /// </summary>
    private static string JoinStructured(string value, string separator)
    {
        var components = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                current.Append(c).Append(value[++i]);  // keep the escape pair intact
            }
            else if (c == ';')
            {
                components.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        components.Add(current.ToString());

        return string.Join(separator, components
            .Select(UnescapeStructuredText)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0));
    }

    /// <summary>
    /// Like <see cref="UnfoldContentLines"/> but also handles the vCard 2.1
    /// quoted-printable soft line break: a physical line ending in a bare '='
    /// on a QUOTED-PRINTABLE property continues onto the next line with the '='
    /// and newline removed (RFC 2045 §6.7). This is a *different* mechanism from
    /// RFC 6350 whitespace folding and is why iCalendar keeps using the plain
    /// unfolder — ICS never uses QP, and a trailing '=' there is literal. Guarded
    /// on the property head declaring QUOTED-PRINTABLE so a stray '=' at the end
    /// of a URL or base64 line stays literal.
    /// </summary>
    private static List<string> UnfoldVCardLines(string text)
    {
        var normalised = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var result = new List<string>();
        var current = new StringBuilder();
        bool awaitingQpContinuation = false;

        foreach (var line in normalised.Split('\n'))
        {
            if (awaitingQpContinuation)
            {
                current.Append(line);                       // QP soft break: append verbatim
            }
            else if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                current.Append(line, 1, line.Length - 1);   // RFC 6350 whitespace fold
            }
            else
            {
                if (current.Length > 0) result.Add(current.ToString());
                current.Clear();
                current.Append(line);
            }

            awaitingQpContinuation = current.Length > 0
                && current[current.Length - 1] == '='
                && LineIsQuotedPrintable(current);
            if (awaitingQpContinuation) current.Length -= 1;  // drop the soft-break '='
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>True if the property head (everything before the first ':') declares
    /// a QUOTED-PRINTABLE encoding, so a trailing '=' is a soft break not a literal.</summary>
    private static bool LineIsQuotedPrintable(StringBuilder line)
    {
        int colon = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == ':') { colon = i; break; }
        }
        var head = colon < 0 ? line.ToString() : line.ToString(0, colon);
        return head.Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decode a quoted-printable value (RFC 2045 §6.7): <c>=XX</c> hex escapes
    /// become the byte 0xXX (so <c>=0D=0A</c> is a CR/LF pair), everything else is
    /// literal. The resulting bytes are read as UTF-8, falling back to
    /// Windows-1252 — the two charsets these legacy cards actually use. (Soft
    /// line breaks are already resolved by <see cref="UnfoldVCardLines"/>.)
    /// </summary>
    private static string QpDecode(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '=' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add((byte)((Uri.FromHex(value[i + 1]) << 4) | Uri.FromHex(value[i + 2])));
                i += 2;
            }
            else if (c < 128)
            {
                bytes.Add((byte)c);
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
        }

        var arr = bytes.ToArray();
        return TryDecode(arr, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true))
               ?? Windows1252.GetString(arr);
    }

    /// <summary>
    /// Line unfolding shared by iCalendar (RFC 5545 §3.1) and vCard (RFC 6350
    /// §3.2), which fold identically: a line beginning with a space or tab is a
    /// continuation of the previous logical line, with that leading whitespace
    /// removed. Without this, folded values are corrupted mid-token.
    /// </summary>
    private static List<string> UnfoldContentLines(string text)
    {
        var normalised = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var line in normalised.Split('\n'))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                current.Append(line, 1, line.Length - 1);
            }
            else
            {
                if (current.Length > 0) result.Add(current.ToString());
                current.Clear();
                current.Append(line);
            }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// Split a content line into (property name, raw parameter string, value):
    /// <c>NAME;PARAM=x;PARAM=y:VALUE</c>. Best-effort — the first ':' wins as the
    /// name/value separator, which is correct for the properties we care about
    /// (a colon inside a quoted parameter value is vanishingly rare in real .ics
    /// and only affects ORGANIZER/ATTENDEE CN lookup, which falls back cleanly).
    /// </summary>
    private static (string? Name, string Parameters, string Value) SplitContentLine(string line)
    {
        int colon = line.IndexOf(':');
        if (colon < 0) return (null, string.Empty, string.Empty);

        var head = line[..colon];
        var value = line[(colon + 1)..];
        int semi = head.IndexOf(';');
        var name = (semi < 0 ? head : head[..semi]).Trim().ToUpperInvariant();
        if (name.Length == 0) return (null, string.Empty, string.Empty);
        var parameters = semi < 0 ? string.Empty : head[(semi + 1)..];
        return (name, parameters, value);
    }

    private static string? ParamValue(string parameters, string key)
    {
        foreach (var part in parameters.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) continue;
            if (!part[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            var v = part[(eq + 1)..].Trim().Trim('"');
            return v.Length == 0 ? null : v;
        }
        return null;
    }

    private static string StripMailto(string value)
    {
        var v = value.Trim();
        return v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? v[7..] : v;
    }

    /// <summary>TEXT unescaping shared by iCalendar (RFC 5545 §3.3.11) and vCard
    /// (RFC 6350 §3.4): \\n \\N -&gt; newline, \\, \\; \\\\ literal.</summary>
    private static string UnescapeStructuredText(string value)
    {
        if (value.IndexOf('\\') < 0) return value;
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next switch
                {
                    'n' or 'N' => '\n',
                    ',' => ',',
                    ';' => ';',
                    '\\' => '\\',
                    _ => next,
                });
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string? TryDecode(byte[] bytes, Encoding encoding)
    {
        try { return encoding.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    private static ExtractionResult BuildResult(string raw)
    {
        var normalised = NormaliseExtractedText(raw);
        if (string.IsNullOrWhiteSpace(normalised))
        {
            return new ExtractionResult(null, StatusNoText);
        }
        if (normalised.Length > MaxExtractedTextChars)
        {
            normalised = normalised[..MaxExtractedTextChars];
        }
        return new ExtractionResult(normalised, StatusDone);
    }

    /// <summary>
    /// Light cleanup: strip BOMs / NULs, normalize line endings, collapse runs
    /// of blank lines. Heavier marketing-email noise stripping isn't relevant
    /// for documents — preserve everything else verbatim so the chunker has
    /// real paragraph boundaries to split on.
    /// </summary>
    private static string NormaliseExtractedText(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '\0' || ch == '﻿') continue;
            sb.Append(ch == '\r' ? '\n' : ch);
        }

        var collapsed = new StringBuilder(sb.Length);
        int blankRun = 0;
        foreach (var line in sb.ToString().Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                blankRun++;
                if (blankRun <= 1) collapsed.Append('\n');
                continue;
            }
            blankRun = 0;
            collapsed.Append(trimmed).Append('\n');
        }
        return collapsed.ToString().Trim();
    }

    private static AttachmentFormat ResolveFormat(string? contentType, string? fileName)
    {
        var ct = (contentType ?? string.Empty).ToLowerInvariant();
        var ext = string.IsNullOrEmpty(fileName) ? string.Empty : Path.GetExtension(fileName).ToLowerInvariant();

        if (ct == "application/pdf") return AttachmentFormat.Pdf;
        if (ct == "application/vnd.openxmlformats-officedocument.wordprocessingml.document")
            return AttachmentFormat.Docx;

        // Calendar detection must precede the generic text/ branch. text/calendar
        // starts with "text/" but needs the ICS-aware unfold/field extraction,
        // not raw decode. Crucially the extension check also has to come first:
        // senders mislabel .ics as text/plain (e.g. Tock reservations), and a
        // bare "text/" test would route those to the raw-text path, leaving the
        // VCALENDAR scaffolding in place. Trust either the calendar content-type
        // or a calendar extension.
        if (ct is "text/calendar" or "application/ics" or "application/calendar" or "text/x-vcalendar"
            || ext is ".ics" or ".ical" or ".vcs")
            return AttachmentFormat.Calendar;
        // vCard, same reasoning as calendar: text/vcard starts with "text/" and
        // .vcf files get mislabeled application/octet-stream or text/plain, so
        // both the content-type and extension checks precede the generic text/
        // branch. A proper extractor also matters more here — a raw .vcf can
        // carry a base64 PHOTO blob the field extractor drops.
        if (ct is "text/vcard" or "text/x-vcard" or "application/vcard"
            || ext is ".vcf" or ".vcard")
            return AttachmentFormat.VCard;
        if (ct.StartsWith("text/")) return AttachmentFormat.Text;

        // Fall back to the extension when the sender declared
        // application/octet-stream or no Content-Type (extremely common for
        // PDFs/DOCX from older mailers).
        return ext switch
        {
            ".pdf" => AttachmentFormat.Pdf,
            ".docx" => AttachmentFormat.Docx,
            ".txt" or ".md" or ".csv" or ".log" => AttachmentFormat.Text,
            _ => AttachmentFormat.Unsupported,
        };
    }

    private static byte[] DecodeEntity(MimeEntity entity)
    {
        using var ms = new MemoryStream();
        if (entity is MimePart part && part.Content is not null)
        {
            part.Content.DecodeTo(ms);
        }
        else
        {
            entity.WriteTo(ms);
        }
        return ms.ToArray();
    }

    private enum AttachmentFormat { Unsupported, Pdf, Docx, Calendar, VCard, Text }
}

public sealed record ExtractionResult(string? Text, string Status);
