using System.CommandLine;
using System.Globalization;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mailvec.Cli.Commands;

internal static class StatusCommand
{
    public static Command Build()
    {
        var cmd = new Command("status", "Show archive counts and embedding coverage.");
        cmd.SetAction(_ => Run());
        return cmd;
    }

    private static int Run()
    {
        using var sp = CliServices.Build();
        return Execute(sp, Console.Out);
    }

    /// <summary>Test seam — see <see cref="PurgeDeletedCommand"/> for the pattern.</summary>
    internal static int Execute(IServiceProvider sp, TextWriter @out)
    {
        var migrator = sp.GetRequiredService<SchemaMigrator>();
        migrator.EnsureUpToDate();

        var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        var archive = sp.GetRequiredService<IOptions<ArchiveOptions>>().Value;
        var ingest = sp.GetRequiredService<IOptions<IngestOptions>>().Value;
        var ollama = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
        var metadata = sp.GetRequiredService<MetadataRepository>();

        var embedder = sp.GetRequiredService<IOptions<EmbedderOptions>>().Value;
        var (total, deleted, embedded, chunkCount) = ReadCounts(conn);
        // OCR backlog via the shared predicate so this line can never disagree
        // with /health or what the embedder actually OCRs.
        var ocrCounts = sp.GetRequiredService<MessageRepository>().OcrCounts(embedder.ImageOcrMinBytes);
        var schemaModel = metadata.Get("embedding_model") ?? "(not set)";
        var schemaDim = metadata.Get("embedding_dimensions") ?? "(not set)";

        // The unified Mailvec version (Directory.Build.props stamps every
        // binary) + schema version: the two numbers support triage needs first.
        var version = typeof(StatusCommand).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        @out.WriteLine($"Mailvec:     v{version} (schema v{metadata.Get("schema_version") ?? "?"})");
        @out.WriteLine($"Database:    {Mailvec.Core.PathExpansion.Expand(archive.DatabasePath)}");
        @out.WriteLine($"Maildir:     {Mailvec.Core.PathExpansion.Expand(ingest.MaildirRoot)}");
        @out.WriteLine();
        @out.WriteLine($"Messages:    {total:N0} total, {deleted:N0} deleted");
        @out.WriteLine($"Embeddings:  {embedded:N0} / {Math.Max(total - deleted, 0):N0} ({Coverage(embedded, total - deleted)})  [{chunkCount:N0} chunks]");
        // ALWAYS printed, even at zero. Printing only when a backlog exists made
        // silence mean two opposite things — "all caught up" and "OCR has never
        // run" — which is exactly the question an operator has on a quiet corpus
        // and could not answer without waiting for new mail to arrive.
        WriteOcrLines(@out, ocrCounts, metadata, embedder);
        @out.WriteLine();
        @out.WriteLine($"Embed model: schema={schemaModel} ({schemaDim}d)  config={ollama.EmbeddingModel} ({ollama.EmbeddingDimensions}d)");

        // v11 space identity: the stored space id names the vector space; the
        // config-hash comparison catches a vector-affecting setting change
        // (query prefix) that every name-based line above would miss.
        var storedSpaceId = metadata.Get(Mailvec.Core.Embedding.EmbeddingSpace.SpaceIdKey);
        var storedConfigHash = metadata.Get(Mailvec.Core.Embedding.EmbeddingSpace.ConfigHashKey);
        var (cfgSpaceId, cfgConfigHash) = Mailvec.Core.Embedding.EmbeddingSpace.FromOllamaOptions(ollama);
        var hashState = storedConfigHash is null ? "config hash not stamped"
            : storedConfigHash == cfgConfigHash ? "config hash ok"
            : "CONFIG HASH MISMATCH — a vector-affecting setting changed";
        var spaceState = storedSpaceId is null ? "(not stamped)"
            : storedSpaceId == cfgSpaceId ? storedSpaceId
            : $"{storedSpaceId}  [config describes {cfgSpaceId}]";
        @out.WriteLine($"Embed space: {spaceState}  ({hashState})");

        // Artifact digest: observed by the embedder, cleared by switch-model.
        // Status is offline, so it reports the stored observation only; the
        // live comparison happens in the embedder and /health.
        var storedDigest = metadata.Get(Mailvec.Core.Embedding.EmbeddingSpace.ModelDigestKey);
        @out.WriteLine($"Embed artifact: {storedDigest ?? "(not observed yet — stamped on the embedder's next run)"}");
        if (schemaModel != "(not set)" && schemaModel != ollama.EmbeddingModel)
        {
            @out.WriteLine("⚠  Schema/config mismatch — the embedder will refuse to start. Run `mailvec switch-model` to migrate the DB to the configured model.");
        }
        return 0;
    }

    private static (long Total, long Deleted, long Embedded, long Chunks) ReadCounts(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM messages),
              (SELECT COUNT(*) FROM messages WHERE deleted_at IS NOT NULL),
              (SELECT COUNT(*) FROM messages WHERE embedded_at IS NOT NULL AND deleted_at IS NULL),
              (SELECT COUNT(*) FROM chunks)
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    /// <summary>
    /// The OCR stage in three lines: backlog, last outcome, and anything
    /// retired. Deliberately unconditional — see the call site.
    /// </summary>
    private static void WriteOcrLines(
        TextWriter @out, OcrStageCounts counts, MetadataRepository metadata, EmbedderOptions embedder)
    {
        if (!embedder.OcrEnabled && !embedder.ImageOcrEnabled)
        {
            @out.WriteLine("OCR:         disabled (Embedder:OcrEnabled and :ImageOcrEnabled are both false)");
            return;
        }

        var parts = new List<string>(2);
        if (counts.PdfPending > 0) parts.Add($"{counts.PdfPending:N0} scanned PDF(s)");
        if (counts.ImagePending > 0) parts.Add($"{counts.ImagePending:N0} image(s)");
        @out.WriteLine(counts.Pending > 0
            ? $"OCR pending: {string.Join(" + ", parts)} awaiting text recovery"
            : "OCR pending: none — backlog drained");

        // The outcome line. "Last success" is the only signal that separates a
        // drained queue from a pass that is silently failing everything; the
        // backlog count cannot, because both read zero-and-not-moving.
        var lastDecision = metadata.Get(OcrHealthKeys.LastDecisionAt);
        var lastSuccess = metadata.Get(OcrHealthKeys.LastSuccessAt);
        var lastFailure = metadata.Get(OcrHealthKeys.LastFailureAt);
        var failKind = metadata.Get(OcrHealthKeys.LastFailureKind);
        var pages = metadata.Get(OcrHealthKeys.PagesSentTotal);

        // "Working" and "recovering text" are different questions. A backlog of
        // textless photos drains perfectly while recovering nothing, so the
        // liveness line reports the last DECISION and mentions the last text
        // recovery separately when the two differ.
        var outcome = string.IsNullOrWhiteSpace(lastDecision) && string.IsNullOrWhiteSpace(lastSuccess)
            // Unknown, not broken — a fresh deployment has simply never OCR'd.
            ? "no OCR activity on record yet"
            : $"last processed {Ago(lastDecision ?? lastSuccess)}";
        if (!string.IsNullOrWhiteSpace(lastSuccess))
            outcome += $"; last text recovered {Ago(lastSuccess)}";
        if (!string.IsNullOrWhiteSpace(lastFailure))
        {
            var kind = string.IsNullOrWhiteSpace(failKind) ? "failure" : failKind;
            outcome += $"; last failure {Ago(lastFailure)} ({kind})";
        }
        if (!string.IsNullOrWhiteSpace(pages) && pages != "0")
            outcome += $"; {pages} page(s) sent";
        @out.WriteLine($"OCR status:  {outcome}");

        // Retired documents are permanent — nothing re-selects a 'failed' row —
        // so they get their own line rather than hiding inside a total.
        if (counts.Retired > 0)
            @out.WriteLine($"OCR failed:  {counts.Retired:N0} document(s) given up on (not retried; see `mailvec reocr --include-failed`)");
    }

    /// <summary>Render an ISO timestamp as a coarse age, which is what a human reads for.</summary>
    private static string Ago(string? iso)
    {
        if (!DateTimeOffset.TryParse(
                iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
            return "at an unreadable timestamp";
        var d = DateTimeOffset.UtcNow - when;
        if (d < TimeSpan.Zero) return "just now";
        if (d < TimeSpan.FromMinutes(1)) return $"{(int)d.TotalSeconds}s ago";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}m ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    private static string Coverage(long covered, long total) =>
        total == 0 ? "n/a" : $"{(double)covered / total:P0}";
}
