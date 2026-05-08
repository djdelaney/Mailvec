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
/// and plain text (UTF-8 with Windows-1252 fallback). Anything else returns
/// status='unsupported' with no text. The result is meant to feed the embedder,
/// not to be displayed to the user — light normalization (whitespace,
/// invisible chars) is good enough.
///
/// Failure modes are explicit so the indexer can stamp a terminal status and
/// not retry forever:
/// - 'done'        — text extracted successfully (may still be empty if the
///                   document is genuinely empty after stripping; the indexer
///                   uses 'no_text' for that case).
/// - 'no_text'     — the format was supported but produced no usable text
///                   (e.g. scanned/image-only PDF — we don't OCR).
/// - 'unsupported' — content type / extension not in our handler list.
/// - 'oversize'    — exceeds Indexer:AttachmentMaxBytes; not even attempted.
/// - 'encrypted'   — PDF / DOCX is password-protected.
/// - 'failed'      — parser threw on otherwise-supported input (corrupt file).
///
/// All stable status values; persisted in attachments.extraction_status.
/// </summary>
public sealed class AttachmentTextExtractor(
    IOptions<IndexerOptions> indexerOptions,
    ILogger<AttachmentTextExtractor> logger)
{
    public const string StatusDone = "done";
    public const string StatusNoText = "no_text";
    public const string StatusUnsupported = "unsupported";
    public const string StatusOversize = "oversize";
    public const string StatusEncrypted = "encrypted";
    public const string StatusFailed = "failed";

    private const int MaxExtractedTextChars = 2_000_000;

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
        decoded ??= TryDecode(bytes, Encoding.GetEncoding("windows-1252"));
        if (decoded is null) return new ExtractionResult(null, StatusFailed);
        return BuildResult(decoded);
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
        if (ct == "application/pdf") return AttachmentFormat.Pdf;
        if (ct == "application/vnd.openxmlformats-officedocument.wordprocessingml.document")
            return AttachmentFormat.Docx;
        if (ct.StartsWith("text/")) return AttachmentFormat.Text;

        // Fall back to the extension when the sender declared
        // application/octet-stream or no Content-Type (extremely common for
        // PDFs/DOCX from older mailers).
        var ext = string.IsNullOrEmpty(fileName) ? string.Empty : Path.GetExtension(fileName).ToLowerInvariant();
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

    private enum AttachmentFormat { Unsupported, Pdf, Docx, Text }
}

public sealed record ExtractionResult(string? Text, string Status);
