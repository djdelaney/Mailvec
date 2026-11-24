using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;
using MimeKit;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Mailvec.Core.Attachments;

/// <summary>
/// Recovers plain text from email attachments so it can be embedded for
/// semantic search. Currently handles PDF (PdfPig), Office Open XML — DOCX / XLSX
/// / PPTX (DocumentFormat.OpenXml) — iCalendar (.ics / VCALENDAR), vCard (.vcf /
/// VCARD), and plain text (UTF-8 with Windows-1252 fallback). Anything else returns
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
/// <remarks>
/// Not sealed, and <see cref="Extract"/> is virtual, purely as a test seam:
/// `mailvec extract-attachments` must never call this while holding SQLite's
/// writer lock (BeginTransaction issues BEGIN IMMEDIATE, and a PdfPig parse
/// under that lock stalls the indexer, embedder, OCR write-back and MCP
/// startup migration). The only way to pin that structurally is to let a test
/// subclass observe, from inside Extract, whether the lock is free. Production
/// code has one implementation — do not add another.
/// </remarks>
public class AttachmentTextExtractor(
    long attachmentMaxBytes,
    ILogger<AttachmentTextExtractor> logger)
{
    public const string StatusDone = ExtractionStatus.Done;
    public const string StatusNoText = ExtractionStatus.NoText;
    public const string StatusOcr = ExtractionStatus.Ocr;
    public const string StatusUnsupported = ExtractionStatus.Unsupported;
    public const string StatusOversize = ExtractionStatus.Oversize;
    public const string StatusEncrypted = ExtractionStatus.Encrypted;
    public const string StatusFailed = ExtractionStatus.Failed;

    private const int MaxExtractedTextChars = 2_000_000;

    /// <summary>
    /// Wall-clock ceiling on one attachment's text extraction, checked between
    /// PDF pages and while streaming PPTX slides.
    /// </summary>
    /// <remarks>
    /// The size gates bound bytes in, not work done. Measured before this
    /// existed: a 25 KB PDF whose 40 pages share one content stream took 22 s
    /// (cost is linear in page objects at ~130 bytes each, so 25 MB buys
    /// hours), and a 166 KB PPTX of empty shapes took 7 s. Extraction runs
    /// inside the scan, so on the in-process (macOS) install that time is time
    /// nothing else is indexed. Over budget we keep whatever text was already
    /// collected and stop; with none, the attachment is <c>failed</c> rather
    /// than <c>no_text</c>, so a PDF that ran out the clock is not then handed
    /// to the OCR pass as if it were a scan. It cannot interrupt a single page
    /// or part: one page whose stream inflates to hundreds of MB still runs to
    /// completion. Internal and per-instance so a test can exhaust it.
    /// </remarks>
    internal TimeSpan ExtractionBudget { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>XML nodes between budget checks while streaming a slide: often enough to be prompt, rare enough to be free.</summary>
    private const int NodesPerBudgetCheck = 4096;

    private const string DrawingMlNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>
    /// Ceiling on the characters in any single Open XML part we load. Applied to
    /// DOCX, XLSX and PPTX through <see cref="OfficeOpenSettings"/>.
    /// </summary>
    /// <remarks>
    /// Office formats are ZIP containers, so <c>Indexer:AttachmentMaxBytes</c>
    /// bounds the COMPRESSED input and says nothing about how far the XML
    /// expands. Measured: a 0.95 MB DOCX whose <c>document.xml</c> is 278 MB of
    /// repeated paragraphs allocated 2,451 MB and took 9.1 s — already past the
    /// indexer's 2 GB container limit at 1/26th of the 25 MB input ceiling. The
    /// same document with this setting throws in 0.4 s having allocated 66 MB.
    ///
    /// <see cref="MaxExtractedTextChars"/> is NOT protection against this, which
    /// is the trap: the break it drives fires (the run above stopped at exactly
    /// 2,000,000 chars) but only after the whole part DOM is materialized, so
    /// the code reads as bounded while allocating without limit.
    ///
    /// The consequence being avoided is a wedge, not just a crash. Extraction
    /// runs inside the scan, before any row is written, so an OOM-kill commits
    /// nothing; <c>restart: unless-stopped</c> brings the indexer back and it
    /// parses the same attachment again. The container memory limit bounds the
    /// blast radius and converts it into a permanent stall of ALL indexing,
    /// triggered by anyone who can send the user mail.
    ///
    /// 32M is ~16x <see cref="MaxExtractedTextChars"/>, which covers heavy
    /// WordprocessingML markup around the most text we would keep anyway, and
    /// costs ~300 MB at the ceiling (the run above works out at ~9 bytes
    /// allocated per XML character). Raising it trades headroom for documents
    /// we would truncate regardless; lowering it starts failing real documents.
    /// **Never set it to 0** — that is the SDK's "unlimited", i.e. the bug.
    /// </remarks>
    private const int MaxCharactersInOfficePart = 32_000_000;

    /// <summary>
    /// One settings instance for all three Open XML formats, so the DOCX, XLSX
    /// and PPTX paths cannot drift apart on the limit above. Internal so a test
    /// can pin it non-zero.
    /// </summary>
    internal static readonly OpenSettings OfficeOpenSettings = new() { MaxCharactersInPart = MaxCharactersInOfficePart };

    /// <summary>
    /// For <see cref="StreamPart"/>, which reads each part's stream itself and
    /// so is not covered by <see cref="OfficeOpenSettings"/>. Same per-part
    /// ceiling, no DTDs, no external resolution. Whitespace is kept: a run that
    /// is only a space is how Word separates two words.
    /// </summary>
    private static readonly XmlReaderSettings PartReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxCharactersInOfficePart,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <summary>
    /// Deepest element nesting accepted in an Open XML part. Real documents
    /// nest a few dozen levels (tables in tables in text boxes); see
    /// <see cref="StreamPart"/> for what unbounded depth cost.
    /// </summary>
    internal const int MaxXmlDepth = 256;

    // Limits on the package's own metadata; see OfficePackageMetadataWithinLimits.
    private const int MaxOfficePackageEntries = 10_000;
    private const long MaxOfficeMetadataEntryBytes = 2 * 1024 * 1024;
    private const long MaxOfficeMetadataTotalBytes = 8 * 1024 * 1024;

    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string StrictRelationshipsNamespace = "http://purl.oclc.org/ooxml/officeDocument/relationships";

    // .NET ships only UTF-*, ASCII, and Latin1 wired up by default — legacy
    // codepages like windows-1252 need CodePagesEncodingProvider registered
    // first (the type is in the shared framework, no package needed),
    // or Encoding.GetEncoding("windows-1252") throws ArgumentException. Do it
    // once per process in the static ctor and cache the resolved encoding so the
    // per-attachment decode fallback (text/plain + calendar) never re-resolves.
    private static readonly Encoding Windows1252;

    static AttachmentTextExtractor()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Windows1252 = Encoding.GetEncoding("windows-1252");
    }

    private readonly long _maxBytes = attachmentMaxBytes;

    public virtual ExtractionResult Extract(MimeEntity entity, string? fileName, string? contentType, long? declaredSize)
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
            logger.LogDebug("Skipping oversized {Ext} attachment: {Size}b > {Max}b", FileKind(fileName), sz, _maxBytes);
            return new ExtractionResult(null, StatusOversize);
        }

        byte[] bytes;
        try
        {
            bytes = DecodeEntity(entity);
        }
        catch (Exception ex)
        {
            // Expected-ish (malformed transfer-encoding); the exception message can
            // echo raw content bytes, so log only its type, not the full exception.
            logger.LogWarning("Failed to decode {Ext} attachment: {Error}", FileKind(fileName), ex.GetType().Name);
            return new ExtractionResult(null, StatusFailed);
        }

        if (bytes.LongLength > _maxBytes)
        {
            return new ExtractionResult(null, StatusOversize);
        }

        // The declared MIME charset only matters for the three text-shaped
        // formats — the office/PDF containers carry their own encoding.
        var declaredCharset = entity.ContentType?.Charset;

        return format switch
        {
            AttachmentFormat.Pdf => ExtractPdf(bytes, fileName),
            AttachmentFormat.Docx => ExtractDocx(bytes, fileName),
            AttachmentFormat.Xlsx => ExtractXlsx(bytes, fileName),
            AttachmentFormat.Pptx => ExtractPptx(bytes, fileName),
            AttachmentFormat.Calendar => ExtractCalendar(bytes, declaredCharset),
            AttachmentFormat.VCard => ExtractVCard(bytes, declaredCharset),
            AttachmentFormat.Text => ExtractText(bytes, declaredCharset),
            _ => new ExtractionResult(null, StatusUnsupported),
        };
    }

    /// <summary>
    /// A non-identifying descriptor for extraction-failure logs. Attachment
    /// filenames are PII (e.g. "2024_Tax_Return_JaneDoe.pdf", "Divorce.docx")
    /// and these logs fire unconditionally at Warning into the indexer's rolling
    /// file, so we log only the extension — enough to tell which format is
    /// failing without naming anyone's documents.
    /// </summary>
    private static string FileKind(string? fileName)
    {
        var ext = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(ext) ? "(no extension)" : ext.ToLowerInvariant();
    }

    /// <summary>
    /// The verdict for an extraction that ran out <see cref="ExtractionBudget"/>:
    /// the text gathered so far if there is any (truncated, like a document over
    /// <see cref="MaxExtractedTextChars"/>), otherwise <c>failed</c>.
    /// </summary>
    private ExtractionResult OverBudget(StringBuilder collected, string? fileName)
    {
        logger.LogWarning(
            "Text extraction for {Ext} attachment stopped after the {Seconds:0}s budget with {Chars} chars collected",
            FileKind(fileName), ExtractionBudget.TotalSeconds, collected.Length);
        var partial = BuildResult(collected.ToString());
        return partial.Status == StatusDone ? partial : new ExtractionResult(null, StatusFailed);
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
            var clock = Stopwatch.StartNew();
            foreach (Page page in doc.GetPages())
            {
                if (sb.Length >= MaxExtractedTextChars) break;
                if (clock.Elapsed > ExtractionBudget) return OverBudget(sb, fileName);
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
            // noise since we already know the cause class. Log the exception
            // type, not ex.Message: PdfPig's message can quote offending bytes
            // from the document. The attachment gets stamped 'failed' and the
            // embedder won't retry.
            logger.LogWarning("PDF text extraction failed for {Ext} attachment: {Error}", FileKind(fileName), ex.GetType().Name);
            return new ExtractionResult(null, StatusFailed);
        }
        catch (Exception ex)
        {
            // Unexpected — keep where it was thrown so we can dig in, but not
            // the exception's message (a parser's can quote the document) and
            // not the filename (PII); the extension is enough to correlate.
            logger.LogWarning("PDF text extraction failed unexpectedly for {Ext} attachment: {Error} {Stack}",
                FileKind(fileName), ex.GetType().Name, ex.StackTrace);
            return new ExtractionResult(null, StatusFailed);
        }
    }

    /// <summary>
    /// Extract text from a .docx: one line per paragraph of the main document
    /// body. Streamed, never <c>MainDocumentPart.Document</c>; see
    /// <see cref="StreamPart"/> for why no Open XML part is DOM-loaded here.
    /// </summary>
    private ExtractionResult ExtractDocx(byte[] bytes, string? fileName)
    {
        try
        {
            if (!OfficePackageMetadataWithinLimits(bytes)) return OversizedPackageMetadata(fileName);
            using var ms = new MemoryStream(bytes, writable: false);
            using var doc = WordprocessingDocument.Open(ms, isEditable: false, OfficeOpenSettings);
            if (doc.MainDocumentPart is not { } main) return new ExtractionResult(null, StatusNoText);

            var sb = new StringBuilder(Math.Min(bytes.Length, 65_536));
            var line = new StringBuilder();
            var clock = Stopwatch.StartNew();
            // Paragraphs are kept apart (one per line) for the chunker's
            // paragraph-aware splitting. A paragraph's text is every text node
            // inside it — runs, deleted runs, field codes — which is what the
            // SDK's InnerText returned when this walked the DOM. A paragraph
            // nested in another (a text box) ends the line it is on.
            bool inBody = false, inTextLeaf = false;
            int paragraphs = 0;
            var read = StreamPart(main, clock, sb, reader =>
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        bool word = IsWordprocessingMl(reader.NamespaceURI);
                        inTextLeaf = word && !reader.IsEmptyElement && reader.LocalName is "t" or "delText" or "instrText";
                        if (!word || reader.IsEmptyElement) break;
                        if (reader.LocalName == "body") inBody = true;
                        else if (inBody && reader.LocalName == "p")
                        {
                            // The outer paragraph's text so far is its own line,
                            // not a prefix glued onto the nested one's.
                            if (paragraphs > 0) FlushLine(line, sb);
                            paragraphs++;
                        }
                        break;
                    case XmlNodeType.EndElement:
                        inTextLeaf = false;
                        if (!IsWordprocessingMl(reader.NamespaceURI)) break;
                        if (reader.LocalName == "body") inBody = false;
                        else if (reader.LocalName == "p" && paragraphs > 0)
                        {
                            paragraphs--;
                            FlushLine(line, sb);
                        }
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA when paragraphs > 0:
                    case XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace when paragraphs > 0 && inTextLeaf:
                        line.Append(reader.Value);
                        // A paragraph that never ends must not outgrow the most text we keep.
                        if (line.Length >= MaxExtractedTextChars) FlushLine(line, sb);
                        break;
                }
            });
            if (read == PartRead.OverBudget)
            {
                FlushLine(line, sb); // keep the paragraph the clock ran out in
                return OverBudget(sb, fileName);
            }

            return BuildResult(sb.ToString());
        }
        catch (FileFormatException ex) when (ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
        {
            return new ExtractionResult(null, StatusEncrypted);
        }
        catch (Exception ex)
        {
            return OfficeExtractionFailed("DOCX", fileName, ex);
        }
    }

    /// <summary>
    /// Extract text from an .xlsx workbook: the sheet names plus the shared-string
    /// table. Excel interns every text cell into the workbook's shared-string
    /// table, so walking it captures all searchable text cheaply — no need to load
    /// each worksheet part (which keeps memory bounded on large books). Numeric
    /// cells are skipped (low search value); inline-string cells — rare, mostly
    /// from programmatic exporters rather than Excel — are not covered.
    /// </summary>
    private ExtractionResult ExtractXlsx(byte[] bytes, string? fileName)
    {
        try
        {
            if (!OfficePackageMetadataWithinLimits(bytes)) return OversizedPackageMetadata(fileName);
            using var ms = new MemoryStream(bytes, writable: false);
            using var doc = SpreadsheetDocument.Open(ms, isEditable: false, OfficeOpenSettings);
            if (doc.WorkbookPart is not { } workbookPart) return new ExtractionResult(null, StatusNoText);

            var sb = new StringBuilder(Math.Min(bytes.Length, 65_536));
            var clock = Stopwatch.StartNew();

            // Sheet names are often the most descriptive labels ("Guest List",
            // "Budget", "Vendors"): workbook > sheets > sheet/@name.
            var read = StreamPart(workbookPart, clock, sb, reader =>
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != 2) return;
                if (reader.LocalName != "sheet" || !IsSpreadsheetMl(reader.NamespaceURI)) return;
                var name = reader.GetAttribute("name");
                if (!string.IsNullOrWhiteSpace(name)) sb.AppendLine(name);
            });

            if (read == PartRead.Completed && workbookPart.SharedStringTablePart is { } strings)
            {
                // One line per shared-string item: sst > si, and every text
                // node under it (plain, rich-text runs, phonetic runs).
                var line = new StringBuilder();
                bool inItem = false, inTextLeaf = false;
                read = StreamPart(strings, clock, sb, reader =>
                {
                    switch (reader.NodeType)
                    {
                        case XmlNodeType.Element:
                            bool sheet = IsSpreadsheetMl(reader.NamespaceURI);
                            inTextLeaf = sheet && !reader.IsEmptyElement && reader.LocalName == "t";
                            if (sheet && reader.Depth == 1 && reader.LocalName == "si" && !reader.IsEmptyElement) inItem = true;
                            break;
                        case XmlNodeType.EndElement:
                            inTextLeaf = false;
                            if (inItem && reader.Depth == 1)
                            {
                                inItem = false;
                                FlushLine(line, sb);
                            }
                            break;
                        case XmlNodeType.Text or XmlNodeType.CDATA when inItem:
                        case XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace when inItem && inTextLeaf:
                            line.Append(reader.Value);
                            if (line.Length >= MaxExtractedTextChars) FlushLine(line, sb);
                            break;
                    }
                });
                if (read == PartRead.OverBudget) FlushLine(line, sb); // keep the item the clock ran out in
            }
            if (read == PartRead.OverBudget) return OverBudget(sb, fileName);

            return BuildResult(sb.ToString());
        }
        catch (FileFormatException ex) when (ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
        {
            return new ExtractionResult(null, StatusEncrypted);
        }
        catch (Exception ex)
        {
            return OfficeExtractionFailed("XLSX", fileName, ex);
        }
    }

    /// <summary>
    /// Extract text from a .pptx deck: every slide's text runs in presentation
    /// order. All visible slide text — titles, bullets, text boxes, table cells —
    /// is carried in Drawing <c>a:t</c> runs, so a descendant walk per slide
    /// collects it. Speaker notes are not included.
    /// </summary>
    private ExtractionResult ExtractPptx(byte[] bytes, string? fileName)
    {
        try
        {
            if (!OfficePackageMetadataWithinLimits(bytes)) return OversizedPackageMetadata(fileName);
            using var ms = new MemoryStream(bytes, writable: false);
            using var doc = PresentationDocument.Open(ms, isEditable: false, OfficeOpenSettings);
            if (doc.PresentationPart is not { } presentationPart) return new ExtractionResult(null, StatusNoText);

            var sb = new StringBuilder(Math.Min(bytes.Length, 65_536));
            var clock = Stopwatch.StartNew();

            // Presentation order is the slide id list (presentation > sldIdLst
            // > sldId/@r:id), not the order of the slide parts in the package.
            var slideRelIds = new List<string>();
            var read = StreamPart(presentationPart, clock, sb, reader =>
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != 2) return;
                if (reader.LocalName != "sldId" || !IsPresentationMl(reader.NamespaceURI)) return;
                var relId = reader.GetAttribute("id", RelationshipsNamespace) ?? reader.GetAttribute("id", StrictRelationshipsNamespace);
                // The package's entry cap bounds real decks; this bounds a list of ids with no slides behind them.
                if (relId is not null && slideRelIds.Count < MaxOfficePackageEntries) slideRelIds.Add(relId);
            });
            if (read == PartRead.OverBudget) return OverBudget(sb, fileName);
            if (slideRelIds.Count == 0) return new ExtractionResult(null, StatusNoText);

            foreach (var relId in slideRelIds)
            {
                if (sb.Length >= MaxExtractedTextChars) break;
                if (clock.Elapsed > ExtractionBudget) return OverBudget(sb, fileName);
                if (!presentationPart.TryGetPartById(relId, out var part) || part is not SlidePart slidePart) continue;

                bool wroteAny = false;
                bool inTextRun = false;
                read = StreamPart(slidePart, clock, sb, reader =>
                {
                    switch (reader.NodeType)
                    {
                        case XmlNodeType.Element:
                            // a:t, the DrawingML text run every visible string lives in.
                            inTextRun = !reader.IsEmptyElement
                                && reader.LocalName == "t"
                                && IsDrawingMl(reader.NamespaceURI);
                            break;
                        case XmlNodeType.EndElement:
                            inTextRun = false;
                            break;
                        case XmlNodeType.Text or XmlNodeType.CDATA when inTextRun:
                            var text = reader.Value;
                            if (string.IsNullOrWhiteSpace(text)) break;
                            sb.Append(text).Append(' ');
                            wroteAny = true;
                            break;
                    }
                });
                if (read == PartRead.OverBudget) return OverBudget(sb, fileName);
                if (wroteAny) sb.AppendLine();
            }

            return BuildResult(sb.ToString());
        }
        catch (FileFormatException ex) when (ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
        {
            return new ExtractionResult(null, StatusEncrypted);
        }
        catch (Exception ex)
        {
            return OfficeExtractionFailed("PPTX", fileName, ex);
        }
    }

    private enum PartRead { Completed, TextCeiling, OverBudget }

    /// <summary>
    /// Reads one Open XML part node by node, calling <paramref name="onNode"/>
    /// for each, until the part ends, <paramref name="collected"/> holds the
    /// most text we keep, or <see cref="ExtractionBudget"/> runs out.
    /// </summary>
    /// <remarks>
    /// Every part is read this way, and none through the SDK's typed DOM
    /// (<c>part.Document</c>, <c>.Workbook</c>, <c>.SharedStringTable</c>,
    /// <c>.Presentation</c>, <c>.Slide</c>), for three reasons the DOM cannot
    /// be configured out of:
    /// <list type="bullet">
    /// <item>The SDK builds its tree by recursion, one stack frame set per
    /// level of nesting, and nothing limits the nesting. Measured: a 6 KB
    /// DOCX of 100,000 nested paragraphs ended the process with a stack
    /// overflow, which no catch block sees. The reader keeps its own stack on
    /// the heap, and anything past <see cref="MaxXmlDepth"/> is refused
    /// here.</item>
    /// <item><c>InnerText</c> on nested elements re-walks each subtree once
    /// per ancestor: 20,000 levels (1.9 KB) took 7 s with no text to trip the
    /// text ceiling and no budget check to stop it.</item>
    /// <item>A loaded tree stays rooted until the package closes, and
    /// <c>MaxCharactersInPart</c> is per part, so a deck of N slides each
    /// just under it allocated N times the ceiling: a 166 KB deck of four
    /// slides of empty shapes reached a 1.5 GB heap.</item>
    /// </list>
    /// The reader is opened on the part's stream, which bypasses
    /// <see cref="OfficeOpenSettings"/>, so <see cref="PartReaderSettings"/>
    /// restates the per-part ceiling.
    /// </remarks>
    private PartRead StreamPart(OpenXmlPart part, Stopwatch clock, StringBuilder collected, Action<XmlReader> onNode)
    {
        int sinceCheck = 0;
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, PartReaderSettings);
        while (reader.Read())
        {
            if (reader.Depth > MaxXmlDepth)
                throw new InvalidDataException("An Open XML part is nested deeper than any real document.");
            if (++sinceCheck >= NodesPerBudgetCheck)
            {
                sinceCheck = 0;
                if (clock.Elapsed > ExtractionBudget) return PartRead.OverBudget;
            }

            onNode(reader);
            if (collected.Length >= MaxExtractedTextChars) return PartRead.TextCeiling;
        }
        return PartRead.Completed;
    }

    private static void FlushLine(StringBuilder line, StringBuilder into)
    {
        if (line.Length == 0) return;
        bool blank = true;
        for (int i = 0; i < line.Length && blank; i++) blank = char.IsWhiteSpace(line[i]);
        if (!blank) into.Append(line).AppendLine();
        line.Clear();
    }

    private static bool IsWordprocessingMl(string ns) =>
        ns is "http://schemas.openxmlformats.org/wordprocessingml/2006/main" or "http://purl.oclc.org/ooxml/wordprocessingml/main";

    private static bool IsSpreadsheetMl(string ns) =>
        ns is "http://schemas.openxmlformats.org/spreadsheetml/2006/main" or "http://purl.oclc.org/ooxml/spreadsheetml/main";

    private static bool IsDrawingMl(string ns) =>
        ns is DrawingMlNamespace or "http://purl.oclc.org/ooxml/drawingml/main";

    private static bool IsPresentationMl(string ns) =>
        ns is "http://schemas.openxmlformats.org/presentationml/2006/main" or "http://purl.oclc.org/ooxml/presentationml/main";

    /// <summary>
    /// False when the package's own bookkeeping — <c>[Content_Types].xml</c>
    /// and the <c>.rels</c> parts — inflates past what any real document
    /// carries, or the archive has an absurd number of entries.
    /// </summary>
    /// <remarks>
    /// <c>System.IO.Packaging</c> parses these while the package is being
    /// OPENED, before <see cref="OfficeOpenSettings"/> applies to anything, and
    /// with no limit of its own. Measured: a 66 KB DOCX with one long attribute
    /// in <c>[Content_Types].xml</c> allocated 1.8 GB, a 408 KB one 9.6 GB.
    /// Sizes are counted while inflating: the length in a ZIP header is the
    /// sender's claim. Bytes that are not a ZIP at all pass, so the SDK still
    /// gets to report an encrypted (OLE) document as encrypted.
    /// </remarks>
    internal static bool OfficePackageMetadataWithinLimits(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxOfficePackageEntries) return false;

            long total = 0;
            var buffer = new byte[81_920];
            foreach (var entry in zip.Entries)
            {
                // Some writers store backslash separators; the name is only matched here, never opened.
                var name = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (!name.StartsWith("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains(".rels/", StringComparison.OrdinalIgnoreCase)) continue;

                long size = 0;
                using var stream = entry.Open();
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    size += n;
                    total += n;
                    if (size > MaxOfficeMetadataEntryBytes || total > MaxOfficeMetadataTotalBytes) return false;
                }
            }
            return true;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private ExtractionResult OversizedPackageMetadata(string? fileName)
    {
        logger.LogWarning("Refusing {Ext} attachment: its package metadata is larger than any real document's", FileKind(fileName));
        return new ExtractionResult(null, StatusFailed);
    }

    /// <summary>
    /// Type name only. The SDK's messages quote the document — a part URI, an
    /// attribute value — and a crafted package makes that string as long as it
    /// likes.
    /// </summary>
    private ExtractionResult OfficeExtractionFailed(string format, string? fileName, Exception ex)
    {
        logger.LogWarning("{Format} text extraction failed for {Ext} attachment: {Error}", format, FileKind(fileName), ex.GetType().Name);
        return new ExtractionResult(null, StatusFailed);
    }

    private static ExtractionResult ExtractText(byte[] bytes, string? declaredCharset)
    {
        string? decoded = DecodeTextBytes(bytes, declaredCharset);
        if (decoded is null) return new ExtractionResult(null, StatusFailed);
        return BuildResult(decoded);
    }

    /// <summary>
    /// Shared decode ladder for text-shaped attachments (plain text, iCalendar,
    /// vCard). Order:
    ///
    ///   1. The declared MIME charset (strict), when it names a non-Latin
    ///      encoding. ISO-2022-JP is pure 7-bit (its escape sequences are
    ///      ASCII), so it decodes "successfully" as UTF-8; Shift-JIS / GB2312 /
    ///      EUC-KR / KOI8-R bytes decode "successfully" as Windows-1252 — in
    ///      both cases the old UTF-8→1252 ladder produced mojibake stamped
    ///      'done': silent quality corruption with nothing to ever trigger
    ///      re-extraction (`extract-attachments --reextract-text` is the
    ///      backfill once this is fixed).
    ///   2. Strict UTF-8 — the de-facto default, and the correct reading for
    ///      the one common mislabel in the Latin family (UTF-8 content marked
    ///      iso-8859-1/windows-1252): genuine Latin-1 text is essentially
    ///      never valid multi-byte UTF-8, which is why a declared Latin
    ///      charset does NOT take slot 1.
    ///   3. The declared charset even if Latin (distinguishes 8859-1 from
    ///      1252 in the 0x80–0x9F range), then Windows-1252, which never
    ///      fails — every byte maps.
    /// </summary>
    private static string? DecodeTextBytes(byte[] bytes, string? declaredCharset)
    {
        var declared = ResolveStrictEncoding(declaredCharset);
        if (declared is not null && !IsLatinFamily(declaredCharset!))
        {
            var viaDeclared = TryDecode(bytes, declared);
            if (viaDeclared is not null) return viaDeclared;
        }

        return TryDecode(bytes, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true))
            ?? (declared is not null ? TryDecode(bytes, declared) : null)
            ?? TryDecode(bytes, Windows1252);
    }

    /// <summary>
    /// Encodings where "decoded without error" carries no signal (single-byte
    /// maps accept every byte) or that the UTF-8 step already covers. For
    /// these, strict UTF-8 goes first; for everything else the sender's label
    /// is the best evidence we have.
    /// </summary>
    private static bool IsLatinFamily(string charset) => charset.Trim().ToLowerInvariant() is
        "utf-8" or "utf8" or "us-ascii" or "ascii" or
        "iso-8859-1" or "iso8859-1" or "latin1" or "l1" or "cp819" or
        "windows-1252" or "cp1252" or "x-cp1252";

    /// <summary>
    /// Resolve a MIME charset name to a strict (throw-on-invalid-byte)
    /// encoding, or null when the name is missing/unknown — unknown labels
    /// fall back to the UTF-8→1252 ladder rather than failing extraction.
    /// </summary>
    private static Encoding? ResolveStrictEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return null;
        try
        {
            // CodePagesEncodingProvider is registered in the static ctor, so
            // legacy names (shift_jis, gb2312, koi8-r, iso-2022-jp, …) resolve.
            return Encoding.GetEncoding(charset.Trim(),
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            return null;
        }
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
    private static ExtractionResult ExtractCalendar(byte[] bytes, string? declaredCharset)
    {
        // Same decode ladder as ExtractText — .ics is UTF-8 by spec but legacy
        // exporters (Outlook .vcs) still emit Windows-1252 or a declared
        // regional codepage.
        string? decoded = DecodeTextBytes(bytes, declaredCharset);
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
    private static ExtractionResult ExtractVCard(byte[] bytes, string? declaredCharset)
    {
        string? decoded = DecodeTextBytes(bytes, declaredCharset);
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
        var head = new QpHead();
        bool awaitingQpContinuation = false;

        foreach (var line in normalised.Split('\n'))
        {
            ReadOnlySpan<char> piece;
            if (awaitingQpContinuation)
            {
                piece = line;                               // QP soft break: append verbatim
            }
            else if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                piece = line.AsSpan(1);                     // RFC 6350 whitespace fold
            }
            else
            {
                if (current.Length > 0) result.Add(current.ToString());
                current.Clear();
                head = new QpHead();
                piece = line;
            }
            current.Append(piece);
            head.Append(piece);

            awaitingQpContinuation = current.Length > 0
                && current[current.Length - 1] == '='
                && head.IsQuotedPrintable;
            if (awaitingQpContinuation) current.Length -= 1;  // drop the soft-break '='
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// Whether a logical line's property head (everything before the first ':')
    /// declares QUOTED-PRINTABLE, so a trailing '=' is a soft break not a
    /// literal. Fed each physical piece as it is appended and looks only at
    /// that piece plus a short carry-over, because re-reading the whole logical
    /// line on every physical line made unfolding quadratic: a 240 KB card of
    /// folded lines that never reach a ':' took 9.4 s, four times that per
    /// doubling.
    /// </summary>
    private struct QpHead
    {
        private const string Marker = "QUOTED-PRINTABLE";
        private string _carry;
        private bool _closed;

        public bool IsQuotedPrintable { get; private set; }

        public void Append(ReadOnlySpan<char> piece)
        {
            if (_closed || IsQuotedPrintable) return;

            int colon = piece.IndexOf(':');
            var inHead = colon < 0 ? piece : piece[..colon];
            // The marker can straddle two pieces, so search the tail of what
            // came before joined to this piece's share of the head.
            var window = string.Concat(_carry ?? "", inHead);
            if (window.Contains(Marker, StringComparison.OrdinalIgnoreCase)) IsQuotedPrintable = true;
            _carry = window.Length < Marker.Length ? window : window[^(Marker.Length - 1)..];
            if (colon >= 0) _closed = true;
        }
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
        if (ct == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
            return AttachmentFormat.Xlsx;
        if (ct == "application/vnd.openxmlformats-officedocument.presentationml.presentation")
            return AttachmentFormat.Pptx;

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

        // A binary-format extension overrides a lying text/* content-type before
        // the generic text/ branch below — same precedence as calendar/vCard.
        // Some mailers slap text/plain on every attachment; without this a
        // .pdf/.docx/.xlsx/.pptx would be raw-decoded (the windows-1252 fallback
        // never throws) into mojibake and indexed with status='done', polluting
        // FTS attachment_text and the embedding space.
        switch (ext)
        {
            case ".pdf": return AttachmentFormat.Pdf;
            case ".docx": return AttachmentFormat.Docx;
            case ".xlsx": return AttachmentFormat.Xlsx;
            case ".pptx": return AttachmentFormat.Pptx;
        }

        if (ct.StartsWith("text/")) return AttachmentFormat.Text;

        // Fall back to the extension when the sender declared
        // application/octet-stream or no Content-Type (extremely common for
        // PDFs/DOCX from older mailers).
        return ext switch
        {
            ".pdf" => AttachmentFormat.Pdf,
            ".docx" => AttachmentFormat.Docx,
            ".xlsx" => AttachmentFormat.Xlsx,
            ".pptx" => AttachmentFormat.Pptx,
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

    private enum AttachmentFormat { Unsupported, Pdf, Docx, Xlsx, Pptx, Calendar, VCard, Text }
}
