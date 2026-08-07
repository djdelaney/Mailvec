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
    public void Model_mismatch_fails_and_points_at_switch_model()
    {
        // `switch-model` is the only sanctioned migration (rebuilds the vec0
        // table + metadata in one transaction). `reindex --all` — the advice
        // this message used to give — clears every vector WITHOUT updating
        // metadata, so the embedder still refuses to start afterwards.
        var checks = Run(Report(embeddings: Emb(schemaModel: "nomic-embed-text", schemaDim: 768, mismatch: true)));
        var c = Find(checks, "Embedding model");
        c.Status.ShouldBe("fail");
        c.Detail.ShouldContain("switch-model");
        c.Detail.ShouldNotContain("reindex");
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

    [Fact]
    public void Ollama_up_but_model_not_pulled_points_at_ollama_pull()
    {
        // The single most common fresh-install failure: Ollama runs fine but
        // `ollama pull` was never run. "Unreachable — restart Ollama" advice
        // here sends the user chasing a healthy server.
        var c = Find(Run(Report(ollama: Oll(reachable: false, modelAvailable: false))), "Ollama");
        c.Status.ShouldBe("warn");
        c.Detail.ShouldContain("not pulled");
        c.Detail.ShouldContain("ollama pull mxbai-embed-large");
        c.Detail.ShouldNotContain("unreachable at");
    }

    [Fact]
    public void Ollama_up_with_model_pulled_but_embed_failing_and_no_recent_work_warns()
    {
        // No evidence the model can serve: the embedder has committed nothing.
        // Here the alarming reading is the correct one.
        var c = Find(Run(Report(ollama: Oll(reachable: false, modelAvailable: true))), "Ollama");
        c.Status.ShouldBe("warn");
        c.Detail.ShouldContain("may not be able to load");
        c.Detail.ShouldContain("committed nothing recently");
        c.Detail.ShouldNotContain("unreachable at");
    }

    [Fact]
    public void A_failed_probe_while_the_embedder_is_working_reads_as_contention_not_breakage()
    {
        // The probe is bounded at 5s and must stay there (the compose
        // healthcheck times out at 10s, and /health's own Ollama ping plus its
        // follow-up already spend most of that), so on a CPU-only host behind a
        // busy embedder it times out
        // even though each embed returns in milliseconds. Observed for real: a
        // 0.1s embed by hand while doctor reported "the model can't load", which
        // sent the operator hunting a non-existent fault.
        //
        // A recent committed batch is proof the model loads and serves, so this
        // must not be a warning — an indicator that goes amber during every
        // large re-embed is one nobody reads.
        var busy = new EmbedderHealth(
            LastSuccessAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            LastFailureAt: null, ConsecutiveFailures: 0, LastFailureKind: null, Stuck: false);

        var c = Find(Run(Report(ollama: Oll(reachable: false, modelAvailable: true), embedder: busy)), "Ollama");

        c.Status.ShouldBe("ok");
        c.Detail.ShouldContain("queueing under load");
        c.Detail.ShouldNotContain("can't load");
    }

    [Fact]
    public void A_stale_embedder_success_does_not_excuse_a_failed_probe()
    {
        // The evidence has to be RECENT. An hour-old success says nothing about
        // whether Ollama works now, so this must fall back to the warning.
        var stale = new EmbedderHealth(
            LastSuccessAt: DateTimeOffset.UtcNow.AddHours(-1),
            LastFailureAt: null, ConsecutiveFailures: 0, LastFailureKind: null, Stuck: false);

        var c = Find(Run(Report(ollama: Oll(reachable: false, modelAvailable: true), embedder: stale)), "Ollama");

        c.Status.ShouldBe("warn");
        c.Detail.ShouldContain("may not be able to load");
    }

    // ---------- mbsync sync outcome ----------
    //
    // The check that catches a sidecar which is alive and beating but whose
    // every sync fails. Nothing else in the pipeline can: "Last indexed" only
    // advances when new mail genuinely arrives, so on a quiet mailbox it reads
    // identically whether sync works or has been broken for a week.
    //
    // Called directly rather than through Run(), because AddHealthChecks gates
    // it on InContainer() — which is a property of the machine running the
    // tests, not something a fixture should depend on.

    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Failing_syncs_fail_the_check_and_say_it_is_not_a_dead_container()
    {
        // The whole point of the wording: an operator seeing this must not go
        // looking for a stopped container. The container is fine.
        var mail = new MailHealth(Now.AddHours(-6), 600, SyncStale: true, Known: true);

        var c = DoctorCommand.MbsyncSyncCheck(mail, inContainer: true, now: Now).ShouldNotBeNull();

        c.Status.ShouldBe("fail");
        c.Detail.ShouldContain("NOT a dead container");
        c.Detail.ShouldContain("app password");
        c.Detail.ShouldContain("docker compose logs mbsync");
    }

    [Fact]
    public void A_recent_successful_sync_is_ok()
    {
        var mail = new MailHealth(Now.AddMinutes(-3), 600, SyncStale: false, Known: true);

        var c = DoctorCommand.MbsyncSyncCheck(mail, inContainer: true, now: Now).ShouldNotBeNull();

        c.Status.ShouldBe("ok");
        c.Detail.ShouldContain("3m");
    }

    [Fact]
    public void No_sync_on_record_warns_rather_than_fails()
    {
        // Also the reading for the first minutes of a fresh deploy, and doctor
        // is run by hand rather than paging anyone — so this must not be a
        // fail that cries wolf on every new stack.
        var mail = new MailHealth(null, null, SyncStale: false, Known: false);

        var c = DoctorCommand.MbsyncSyncCheck(mail, inContainer: true, now: Now).ShouldNotBeNull();

        c.Status.ShouldBe("warn");
        c.Detail.ShouldContain("fresh deploy");
    }

    [Fact]
    public void Emits_nothing_outside_a_container()
    {
        // Only the Alpine sidecar writes the marker. On a launchd install the
        // file never exists, so a row here would be a permanent meaningless
        // unknown — mbsync is covered there by the stderr-log check instead.
        DoctorCommand.MbsyncSyncCheck(
            new MailHealth(null, null, SyncStale: false, Known: false),
            inContainer: false, now: Now).ShouldBeNull();

        DoctorCommand.MbsyncSyncCheck(
            new MailHealth(Now.AddHours(-6), 600, SyncStale: true, Known: true),
            inContainer: false, now: Now).ShouldBeNull();
    }

    [Fact]
    public void The_quoted_threshold_matches_the_one_the_verdict_used()
    {
        // Doctor explains the verdict by quoting a window; if it re-derived
        // that number instead of asking MbsyncSyncFile, the two could drift and
        // the message would contradict the status beside it. 600s x 4 = 40m.
        var mail = new MailHealth(Now.AddHours(-6), 600, SyncStale: true, Known: true);

        DoctorCommand.MbsyncSyncCheck(mail, inContainer: true, now: Now)!
            .Detail.ShouldContain("threshold 40m");
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
        OllamaHealth? ollama = null,
        EmbedderHealth? embedder = null)
        => new(
            Status: "ok",
            Version: "0.0.0",
            Database: database ?? Db(),
            Embeddings: embeddings ?? Emb(),
            Ollama: ollama ?? Oll(),
            Embedder: embedder ?? new EmbedderHealth(null, null, 0, null, Stuck: false),
            Ocr: new OcrHealth(Enabled: false, VisionModel: "qwen2.5vl:7b", ModelAvailable: null, Pending: 0, Recovered: 0, ImagePending: 0, ImageRecovered: 0),
            Mail: new MailHealth(LastSyncAt: null, ExpectedIntervalSeconds: null, SyncStale: false, Known: false),
            Services: []);

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

    private static OllamaHealth Oll(bool reachable = true, bool? modelAvailable = null)
        => new("http://localhost:11434", reachable, "mxbai-embed-large", modelAvailable);
}
