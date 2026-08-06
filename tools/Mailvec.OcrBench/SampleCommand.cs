using System.Runtime.Versioning;
using Mailvec.Core.Attachments;
using Mailvec.Core.Models;
using Mailvec.Core.Options;
using Mailvec.Pdf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Mailvec.OcrBench;

/// <summary>
/// Builds a reproducible sample: picks documents out of the frozen archive,
/// reads their bytes from the Maildir, renders every page with the SAME
/// <see cref="PdfRenderer"/> the embedder's OCR pass uses, and writes it all to
/// a working directory.
///
/// The read is strictly read-only against the database and the Maildir; nothing
/// here writes to the archive. (This machine's archive is a frozen eval corpus —
/// see docs/contributing/local-dev-dataset.md.)
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
internal static class SampleCommand
{
    /// <summary>
    /// Minimum reference characters for a page to enter the truth set. Pages
    /// below this are covers, dividers, or image-only pages inside an otherwise
    /// text-layer PDF — scoring an engine against a near-empty reference
    /// produces a meaningless CER (any output is infinite error).
    /// </summary>
    private const int MinReferenceChars = 200;

    public static async Task<int> RunAsync(Args args)
    {
        var workDir = args.Require("work");
        var set = args.Get("set", "truth") switch
        {
            "truth" => SampleSet.Truth,
            "scans" => SampleSet.Scans,
            var other => throw new ArgsException($"--set must be 'truth' or 'scans', got '{other}'."),
        };
        var wantDocs = int.Parse(args.Get("n", "40"));
        var maxPagesPerDoc = int.Parse(args.Get("max-pages", "3"));
        var seed = int.Parse(args.Get("seed", "1"));

        var config = Config.Load();
        var dbPath = config.Archive.DatabasePath;
        Console.Error.WriteLine($"Reading (read-only) {dbPath}");

        var reader = new MaildirAttachmentReader(Options.Create(config.Ingest));

        var candidates = SelectCandidates(dbPath, set, wantDocs * 4, seed);
        Console.Error.WriteLine($"{candidates.Count} candidate {set} documents; materialising up to {wantDocs}.");

        Directory.CreateDirectory(Path.Combine(workDir, "docs"));
        Directory.CreateDirectory(Path.Combine(workDir, "pages"));
        if (set == SampleSet.Truth) Directory.CreateDirectory(Path.Combine(workDir, "ref"));

        var documents = new List<DocumentSample>();
        var skipped = 0;
        foreach (var c in candidates)
        {
            if (documents.Count >= wantDocs) break;
            try
            {
                var doc = Materialise(reader, workDir, set, c, maxPagesPerDoc);
                if (doc is null) { skipped++; continue; }
                documents.Add(doc);
                Console.Error.WriteLine($"  [{documents.Count}/{wantDocs}] a{c.AttachmentId} {c.FileName} — {doc.Pages.Count} page(s)");
            }
            catch (Exception ex)
            {
                skipped++;
                Console.Error.WriteLine($"  skip a{c.AttachmentId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var manifest = new Manifest(
            DateTimeOffset.UtcNow.ToString("O"),
            set,
            new RendererSettings(150, PdfRenderer.MaxEdgePx, 85),
            documents);

        var manifestPath = Path.Combine(workDir, "manifest.json");
        Json.Write(manifestPath, manifest);

        Console.Error.WriteLine();
        Console.Error.WriteLine($"Sample: {documents.Count} documents, {manifest.TotalPages} pages ({skipped} skipped).");
        Console.Error.WriteLine($"Wrote {manifestPath}");
        await Task.CompletedTask;
        return 0;
    }

    private sealed record Candidate(
        long AttachmentId, long MessageId, int PartIndex, string? FileName, long SizeBytes, Message Message);

    /// <summary>
    /// Pull candidate attachments. Deterministic given the seed: ordered by a
    /// hash of (seed, id) so the same seed reproduces the same sample, and a
    /// different seed draws an independent one without re-running anything else.
    /// </summary>
    private static List<Candidate> SelectCandidates(string dbPath, SampleSet set, int limit, int seed)
    {
        // Truth: native-extracted PDFs — the ones with a real text layer.
        // Scans: PDFs the OCR pass already handled — genuine image-only documents.
        var status = set == SampleSet.Truth ? "done" : "ocr";

        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        conn.Open();

        using var cmd = conn.CreateCommand();
        // No sqlite-vec needed (we never touch chunk_embeddings), so this opens
        // the archive without loading the extension.
        cmd.CommandText = """
            SELECT a.id, a.message_id, a.part_index, a.filename, a.size_bytes,
                   m.message_id, m.maildir_path, m.maildir_filename, m.folder
            FROM attachments a
            JOIN messages m ON m.id = a.message_id
            WHERE a.extraction_status = $status
              AND a.content_type LIKE 'application/pdf%'
              AND m.deleted_at IS NULL
              AND LENGTH(COALESCE(a.extracted_text, '')) > 0
            ORDER BY (a.id * 2654435761 + $seed) % 1000003
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$seed", seed);
        cmd.Parameters.AddWithValue("$limit", limit);

        var result = new List<Candidate>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            result.Add(new Candidate(
                rd.GetInt64(0),
                rd.GetInt64(1),
                rd.GetInt32(2),
                rd.IsDBNull(3) ? null : rd.GetString(3),
                rd.IsDBNull(4) ? 0 : rd.GetInt64(4),
                new Message
                {
                    Id = rd.GetInt64(1),
                    MessageId = rd.GetString(5),
                    MaildirPath = rd.GetString(6),
                    MaildirFilename = rd.GetString(7),
                    Folder = rd.GetString(8),
                }));
        }
        return result;
    }

    /// <summary>
    /// Decode one attachment out of its .eml, render its pages, and (for the
    /// truth set) extract the per-page reference text with PdfPig. Returns null
    /// when the document yields no usable pages.
    /// </summary>
    private static DocumentSample? Materialise(
        MaildirAttachmentReader reader, string workDir, SampleSet set, Candidate c, int maxPagesPerDoc)
    {
        // No cap: we want the document whatever its size, and the harness is
        // interactive rather than an unattended service.
        var bytes = reader.ReadBytes(c.Message, PartIndexOf(c), maxBytes: null);

        var pdfRel = Path.Combine("docs", $"a{c.AttachmentId}.pdf");
        File.WriteAllBytes(Path.Combine(workDir, pdfRel), bytes);

        var pdfPageCount = PdfRenderer.PageCount(bytes);

        // Reference text per page, truth set only. ContentOrderTextExtractor is
        // exactly what AttachmentTextExtractor uses, so the reference matches
        // what the indexer would have stored for this page.
        string[]? references = null;
        if (set == SampleSet.Truth)
        {
            references = new string[pdfPageCount];
            using var pdf = PdfDocument.Open(bytes);
            for (var i = 0; i < pdfPageCount; i++)
            {
                Page page = pdf.GetPage(i + 1); // PdfPig pages are 1-based
                references[i] = ContentOrderTextExtractor.GetText(page) ?? string.Empty;
            }
        }

        var pages = new List<PageSample>();
        for (var i = 0; i < pdfPageCount && pages.Count < maxPagesPerDoc; i++)
        {
            var reference = references?[i];
            if (set == SampleSet.Truth && (reference is null || reference.Trim().Length < MinReferenceChars))
                continue; // image-only or near-empty page — nothing to score against

            var imageRel = Path.Combine("pages", $"a{c.AttachmentId}-p{i}.jpg");
            File.WriteAllBytes(Path.Combine(workDir, imageRel), PdfRenderer.RenderPageJpeg(bytes, i));

            string? refRel = null;
            if (reference is not null)
            {
                refRel = Path.Combine("ref", $"a{c.AttachmentId}-p{i}.txt");
                File.WriteAllText(Path.Combine(workDir, refRel), reference);
            }

            pages.Add(new PageSample(i, imageRel, refRel, reference?.Trim().Length ?? 0));
        }

        if (pages.Count == 0)
        {
            File.Delete(Path.Combine(workDir, pdfRel));
            return null;
        }

        return new DocumentSample(c.AttachmentId, c.MessageId, c.FileName, c.SizeBytes, pdfRel, pdfPageCount, pages);
    }

    /// <summary>
    /// The attachment's MIME part index, read from the row. Kept as its own step
    /// because <see cref="MaildirAttachmentReader"/> resolves the part through
    /// <c>MessageParts.Indexable</c> — the writer/reader contract that makes
    /// part_index mean the same thing on both sides.
    /// </summary>
    private static int PartIndexOf(Candidate c) => c.PartIndex;
}
