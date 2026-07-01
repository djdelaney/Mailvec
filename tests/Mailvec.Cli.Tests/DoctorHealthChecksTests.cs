using Mailvec.Cli.Commands;
using Mailvec.Core.Health;

namespace Mailvec.Cli.Tests;

/// <summary>
/// Coverage for DoctorCommand.AddHealthChecks — the pure mapping from a
/// HealthReport to the ok/warn/fail rows the doctor prints. This is the logic
/// that decides whether `mailvec doctor` flags the pipeline as broken; the
/// thresholds (95% / 50% coverage, 1h indexing staleness) live here, so a
/// silent off-by-one would mis-report the system's health. The surrounding
/// Run() (launchctl shell-outs, HTTP probe) stays untested by design.
/// </summary>
public class DoctorHealthChecksTests
{
    // ---------- Embedding model ----------

    [Fact]
    public void Model_mismatch_fails_and_points_at_reindex()
    {
        var checks = Run(Report(embeddings: Emb(schemaModel: "nomic-embed-text", schemaDim: 768, mismatch: true)));
        var c = Find(checks, "Embedding model");
        c.Status.ShouldBe("fail");
        c.Detail.ShouldContain("reindex");
    }

    [Fact]
    public void Unstamped_schema_warns_pending_first_embed()
    {
        var checks = Run(Report(embeddings: Emb(schemaModel: null, schemaDim: null)));
        Find(checks, "Embedding model").Status.ShouldBe("warn");
    }

    [Fact]
    public void Matching_schema_is_ok()
    {
        Find(Run(Report()), "Embedding model").Status.ShouldBe("ok");
    }

    // ---------- Embedding coverage ----------

    [Fact]
    public void No_live_messages_warns()
    {
        var checks = Run(Report(database: Db(total: 0, deleted: 0)));
        var c = Find(checks, "Embedding cover");
        c.Status.ShouldBe("warn");
        c.Detail.ShouldContain("no live messages");
    }

    [Fact]
    public void Coverage_at_or_above_95_is_ok()
    {
        Find(Run(Report(embeddings: Emb(coveragePct: 95.0))), "Embedding cover").Status.ShouldBe("ok");
    }

    [Fact]
    public void Coverage_between_50_and_95_warns_as_in_progress()
    {
        var c = Find(Run(Report(embeddings: Emb(coveragePct: 60.0))), "Embedding cover");
        c.Status.ShouldBe("warn");
        c.Detail.ShouldContain("making progress");
    }

    [Fact]
    public void Coverage_below_50_warns_as_mostly_unindexed()
    {
        var c = Find(Run(Report(embeddings: Emb(coveragePct: 10.0))), "Embedding cover");
        c.Status.ShouldBe("warn");
        c.Detail.ShouldContain("miss most");
    }

    [Fact]
    public void Coverage_counts_only_live_messages()
    {
        // live = total - deleted. 100 total, 40 deleted → 60 live; the detail
        // line should render 60, not 100.
        var checks = Run(Report(
            database: Db(total: 100, deleted: 40),
            embeddings: Emb(coveragePct: 100.0, embedded: 60)));
        Find(checks, "Embedding cover").Detail.ShouldContain("60");
    }

    // ---------- Last indexed ----------

    [Fact]
    public void Recently_indexed_is_ok()
    {
        var checks = Run(Report(database: Db(lastIndexed: DateTimeOffset.UtcNow.AddMinutes(-5))));
        Find(checks, "Last indexed").Status.ShouldBe("ok");
    }

    [Fact]
    public void Stale_index_older_than_an_hour_warns()
    {
        var checks = Run(Report(database: Db(lastIndexed: DateTimeOffset.UtcNow.AddHours(-2))));
        Find(checks, "Last indexed").Status.ShouldBe("warn");
    }

    [Fact]
    public void Never_indexed_warns()
    {
        var checks = Run(Report(database: Db(lastIndexed: null)));
        var c = Find(checks, "Last indexed");
        c.Status.ShouldBe("warn");
        c.Detail.ShouldContain("no messages indexed");
    }

    // ---------- Ollama ----------

    [Fact]
    public void Ollama_skipped_when_no_net_warns()
    {
        var c = Find(Run(Report(), skipNet: true), "Ollama");
        c.Status.ShouldBe("warn");
        c.Detail.ShouldContain("--no-net");
    }

    [Fact]
    public void Ollama_reachable_is_ok()
    {
        Find(Run(Report(ollama: Oll(reachable: true))), "Ollama").Status.ShouldBe("ok");
    }

    [Fact]
    public void Ollama_unreachable_warns_about_degraded_search()
    {
        var c = Find(Run(Report(ollama: Oll(reachable: false))), "Ollama");
        c.Status.ShouldBe("warn");
        c.Detail.ShouldContain("unreachable");
    }

    // ---------- builders ----------

    private static IReadOnlyList<DoctorCommand.DoctorCheck> Run(HealthReport report, bool skipNet = false)
    {
        var checks = new List<DoctorCommand.DoctorCheck>();
        DoctorCommand.AddHealthChecks(checks, report, skipNet);
        return checks;
    }

    private static DoctorCommand.DoctorCheck Find(IReadOnlyList<DoctorCommand.DoctorCheck> checks, string name)
        => checks.Single(c => c.Name == name);

    private static HealthReport Report(
        EmbeddingHealth? embeddings = null,
        DatabaseHealth? database = null,
        OllamaHealth? ollama = null)
        => new(
            Status: "ok",
            Database: database ?? Db(),
            Embeddings: embeddings ?? Emb(),
            Ollama: ollama ?? Oll(),
            Embedder: new EmbedderHealth(null, null, 0, null, Stuck: false),
            Ocr: new OcrHealth(Enabled: false, VisionModel: "qwen2.5vl:7b", ModelAvailable: null, Pending: 0, Recovered: 0, ImagePending: 0, ImageRecovered: 0));

    private static DatabaseHealth Db(long total = 100, long deleted = 0, DateTimeOffset? lastIndexed = null)
        => new("/tmp/archive.sqlite", total, deleted, lastIndexed);

    private static EmbeddingHealth Emb(
        string? schemaModel = "mxbai-embed-large",
        int? schemaDim = 1024,
        bool mismatch = false,
        double coveragePct = 100.0,
        long embedded = 100,
        long chunks = 200)
        => new(schemaModel, schemaDim, "mxbai-embed-large", 1024, mismatch, embedded, coveragePct, chunks);

    private static OllamaHealth Oll(bool reachable = true)
        => new("http://localhost:11434", reachable, "mxbai-embed-large");
}
