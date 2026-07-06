using System.CommandLine;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Mailvec.Core;
using Mailvec.Core.Data;
using Mailvec.Core.Health;
using Mailvec.Core.Options;
using Mailvec.Core.Vision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mailvec.Cli.Commands;

/// <summary>
/// One-stop preflight: walks every dependency Mailvec needs to function
/// (DB, schema, vec0 extension, Maildir, Ollama, launchd agents, MCP HTTP)
/// and prints a single checklist of pass / warn / fail for each. Useful when
/// "search returns nothing" or "the embedder isn't running" — instead of
/// chasing four or five separate diagnostic commands, run `mailvec doctor`
/// and get the whole picture.
///
/// Adopted from the Phase-5 readiness work: as more agents (Gemini CLI,
/// Codex CLI, ChatGPT desktop) start pointing at the same MCP server, users
/// will need a way to triage "which layer is broken — the agent's config,
/// the launchd service, the database, or Ollama?" without knowing the
/// internal architecture. A passing doctor run answers "Mailvec itself is
/// fine; debug your client".
///
/// Read-only against the DB and external services. No state-changing side
/// effects — we deliberately don't run EnsureUpToDate here, since doctor's
/// job is to *report* schema drift, not silently fix it.
///
/// Exit codes: 0 if all checks pass or warn, 1 if any fail. `--json` emits
/// machine-readable output for bug reports / monitoring.
/// </summary>
internal static class DoctorCommand
{
    public static Command Build()
    {
        var jsonOpt = new Option<bool>("--json") { Description = "Emit a machine-readable JSON report instead of the human checklist. Each check has status='ok'|'warn'|'fail' plus a detail string." };
        var skipNetOpt = new Option<bool>("--no-net") { Description = "Skip network checks (Ollama ping, MCP /health). Useful for offline diagnosis." };

        var cmd = new Command("doctor", "Preflight check: DB, schema, vec0, Maildir, Ollama, launchd agents, MCP HTTP. One pass / warn / fail per dependency.")
        {
            jsonOpt,
            skipNetOpt,
        };

        cmd.SetAction(async (parse, ct) =>
        {
            var json = parse.GetValue(jsonOpt);
            var skipNet = parse.GetValue(skipNetOpt);
            return await RunAsync(json, skipNet, ct).ConfigureAwait(false);
        });
        return cmd;
    }

    private static async Task<int> RunAsync(bool json, bool skipNet, CancellationToken ct)
    {
        using var sp = CliServices.Build();

        var checks = new List<DoctorCheck>();

        var archive = sp.GetRequiredService<IOptions<ArchiveOptions>>().Value;
        var ingest = sp.GetRequiredService<IOptions<IngestOptions>>().Value;
        var ollama = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
        var mcpOpts = sp.GetRequiredService<IOptions<McpOptions>>().Value;

        // ---------------------------------------------------------------
        // Configuration / static — checks that don't talk to a service
        // ---------------------------------------------------------------
        var dbPath = PathExpansion.Expand(archive.DatabasePath);
        if (File.Exists(dbPath))
        {
            var size = new FileInfo(dbPath).Length;
            checks.Add(DoctorCheck.Ok("Database", $"{dbPath} ({FormatSize(size)})", "config"));
        }
        else
        {
            // Without a DB the indexer can't write and search returns
            // nothing. Highest-severity finding — fail.
            checks.Add(DoctorCheck.Fail("Database", $"{dbPath} not found. Run the indexer once to bootstrap, or check Archive__DatabasePath.", "config"));
        }

        // Schema version. Read directly from metadata so we don't trigger a
        // migration as a side effect of doctor (we're reporting state, not
        // fixing it). 0 means "fresh DB, schema not yet applied".
        if (File.Exists(dbPath))
        {
            try
            {
                var migrator = sp.GetRequiredService<SchemaMigrator>();
                var current = migrator.GetCurrentVersion();
                if (current == SchemaMigrator.LatestSchemaVersion)
                {
                    checks.Add(DoctorCheck.Ok("Schema", $"version {current} (current)", "config"));
                }
                else if (current == 0)
                {
                    checks.Add(DoctorCheck.Warn("Schema", $"DB has no schema_version row — first indexer run will apply v{SchemaMigrator.LatestSchemaVersion}.", "config"));
                }
                else if (current < SchemaMigrator.LatestSchemaVersion)
                {
                    checks.Add(DoctorCheck.Warn("Schema",
                        $"DB at v{current}, binary expects v{SchemaMigrator.LatestSchemaVersion}. Migrations apply automatically on next service start; v3->v4 also needs `mailvec extract-attachments` to backfill text.",
                        "config"));
                }
                else
                {
                    // Higher than binary expects — likely a rolled-back
                    // binary against a newer DB. Could surface as runtime
                    // errors against unknown columns; flag loudly.
                    checks.Add(DoctorCheck.Fail("Schema",
                        $"DB at v{current}, but this binary only knows v{SchemaMigrator.LatestSchemaVersion}. Either upgrade the binary or restore an older DB snapshot.",
                        "config"));
                }
            }
            catch (Exception ex)
            {
                checks.Add(DoctorCheck.Fail("Schema", $"could not read schema_version: {ex.GetType().Name}: {ex.Message}", "config"));
            }
        }

        // vec0.dylib resolution. Path in ArchiveOptions can be relative
        // (default `./runtimes/osx-arm64/native/vec0.dylib`); look at it
        // both from CWD and from AppContext.BaseDirectory since either is a
        // valid working dir for a published binary.
        //
        // Only expand when the configured path starts with `~` — calling
        // PathExpansion.Expand on a `./...` value would prematurely
        // resolve it against the user's CWD via Path.GetFullPath and break
        // every downstream fallback (Path.Combine short-circuits on
        // absolute paths). Mirrors ConnectionFactory.ResolveVecExtension.
        var configuredVecPath = archive.SqliteVecExtensionPath ?? string.Empty;
        var vecPath = configuredVecPath.StartsWith('~')
            ? PathExpansion.Expand(configuredVecPath)
            : configuredVecPath;
        var vecResolved = ResolveExtensionPath(vecPath);
        if (vecResolved is not null)
        {
            checks.Add(DoctorCheck.Ok("vec0.dylib", vecResolved, "config"));
        }
        else
        {
            checks.Add(DoctorCheck.Fail("vec0.dylib",
                $"not found at '{vecPath}' (relative to CWD or {AppContext.BaseDirectory}). Run ops/fetch-sqlite-vec.sh to download it.",
                "config"));
        }

        // Maildir presence. Warn rather than fail — a fresh install may have
        // configured the path before mbsync's first run, in which case the
        // rest of the report is still useful.
        var maildirRoot = PathExpansion.Expand(ingest.MaildirRoot);
        if (Directory.Exists(maildirRoot))
        {
            checks.Add(DoctorCheck.Ok("Maildir root", maildirRoot, "config"));
        }
        else
        {
            checks.Add(DoctorCheck.Warn("Maildir root",
                $"{maildirRoot} not found. The indexer needs this to ingest mail; check Ingest__MaildirRoot or run mbsync once.",
                "config"));
        }

        // ---------------------------------------------------------------
        // Pipeline — DB-backed signals that depend on data
        // ---------------------------------------------------------------
        if (File.Exists(dbPath))
        {
            try
            {
                // HealthService is the single source of truth for what
                // counts as "healthy" — the MCP /health endpoint and
                // `mailvec doctor` should never disagree. Under --no-net we
                // synthesize a HealthReport without touching Ollama (so the
                // 2s ping doesn't fire) and let AddHealthChecks render it
                // with the Ollama line marked "skipped".
                var report = skipNet
                    ? BuildOfflineHealthReport(sp, archive, ollama)
                    : await sp.GetRequiredService<HealthService>().CheckAsync(ct).ConfigureAwait(false);
                AddHealthChecks(checks, report, skipNet);
            }
            catch (Exception ex)
            {
                checks.Add(DoctorCheck.Fail("Pipeline", $"could not compute health snapshot: {ex.GetType().Name}: {ex.Message}", "pipeline"));
            }

            // Orphan vec0 rows. Cheap COUNT against the same DB, so we run it
            // alongside the health snapshot. A non-zero count means the
            // embedder will hit UNIQUE-constraint failures on the next rowid
            // collision — surface it loudly because the symptom (messages
            // stuck unembedded forever, retry every poll interval) is otherwise
            // invisible from `status`.
            try
            {
                var orphans = sp.GetRequiredService<ChunkRepository>().CountOrphanEmbeddings();
                if (orphans == 0)
                {
                    checks.Add(DoctorCheck.Ok("Orphan vectors", "none", "pipeline"));
                }
                else
                {
                    checks.Add(DoctorCheck.Warn("Orphan vectors",
                        $"{orphans:N0} chunk_embeddings row(s) point to deleted chunks. The embedder will fail with UNIQUE-constraint errors when a new chunk's rowid collides. Run `mailvec repair` to clear them.",
                        "pipeline"));
                }
            }
            catch (Exception ex)
            {
                checks.Add(DoctorCheck.Fail("Orphan vectors", $"could not query: {ex.GetType().Name}: {ex.Message}", "pipeline"));
            }
        }

        // ---------------------------------------------------------------
        // Services — launchd state (macOS only)
        // ---------------------------------------------------------------
        if (OperatingSystem.IsMacOS())
        {
            var launchdLabels = new[] { "com.mailvec.mbsync", "com.mailvec.indexer", "com.mailvec.embedder", "com.mailvec.mcp" };
            var listing = TryReadLaunchctlList();
            foreach (var label in launchdLabels)
            {
                checks.Add(InspectLaunchd(label, listing));
            }
        }
        else
        {
            checks.Add(DoctorCheck.Warn("launchd", $"skipped — only macOS is supported as a host today (running on {Environment.OSVersion.Platform}).", "services"));
        }

        // ---------------------------------------------------------------
        // External tools
        // ---------------------------------------------------------------
        var mbsyncPath = ResolveOnPath("mbsync");
        if (mbsyncPath is not null)
        {
            checks.Add(DoctorCheck.Ok("mbsync", mbsyncPath, "tools"));
        }
        else
        {
            // Only matters if the user wants unattended IMAP sync. Warn,
            // don't fail — Mailvec itself doesn't shell out to mbsync at
            // request time; it just reads the Maildir mbsync writes to.
            checks.Add(DoctorCheck.Warn("mbsync",
                "not on PATH. Mailvec doesn't invoke it at request time, but you'll need it (`brew install isync`) to schedule IMAP sync as a launchd agent.",
                "tools"));
        }

        // mbsync exits 0 even when its sync fails (channel lock, DNS,
        // socket errors), so the launchctl-reported exit code above is
        // not enough on its own. Tail the stderr log and surface anything
        // recent here — same source the tray's mbsync tile reads from.
        checks.Add(InspectMbsyncStderr());

        // ---------------------------------------------------------------
        // OCR (vision) model — when the embedder is set to OCR scanned PDFs,
        // confirm the model is pulled, else they silently never get processed.
        // ---------------------------------------------------------------
        var embedder = sp.GetRequiredService<IOptions<EmbedderOptions>>().Value;
        if (embedder.OcrEnabled && !skipNet)
        {
            try
            {
                var available = await sp.GetRequiredService<IVisionClient>().IsModelAvailableAsync(ct).ConfigureAwait(false);
                checks.Add(available
                    ? DoctorCheck.Ok("OCR model", $"{ollama.VisionModel} available", "pipeline")
                    : DoctorCheck.Warn("OCR model",
                        $"{ollama.VisionModel} not pulled — scanned PDFs won't be OCR'd. " +
                        $"Run `ollama pull {ollama.VisionModel}` or set Embedder:OcrEnabled=false.",
                        "pipeline"));
            }
            catch (Exception ex)
            {
                checks.Add(DoctorCheck.Warn("OCR model", $"could not check vision model ({ex.GetType().Name}).", "pipeline"));
            }
        }
        else if (embedder.OcrEnabled)
        {
            checks.Add(DoctorCheck.Warn("OCR model", "skipped (--no-net)", "pipeline"));
        }

        // ---------------------------------------------------------------
        // MCP HTTP /health — confirms the launchd agent is actually serving
        // ---------------------------------------------------------------
        if (!skipNet)
        {
            var url = $"http://{mcpOpts.BindAddress}:{mcpOpts.Port}/health";
            checks.Add(await ProbeMcpHealthAsync(url, ct).ConfigureAwait(false));
        }
        else
        {
            checks.Add(DoctorCheck.Warn("MCP /health", "skipped (--no-net)", "mcp"));
        }

        // ---------------------------------------------------------------
        // Output
        // ---------------------------------------------------------------
        if (json)
        {
            var (ok, warn, fail) = Summarize(checks);
            var doc = new
            {
                summary = new { ok, warn, fail },
                checks = checks.Select(c => new
                {
                    section = c.Section,
                    name = c.Name,
                    status = c.Status,
                    detail = c.Detail,
                }),
            };
            Console.WriteLine(JsonSerializer.Serialize(doc, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        else
        {
            PrintHumanReport(checks);
        }

        return checks.Any(c => c.Status == "fail") ? 1 : 0;
    }

    // ---------------------------------------------------------------------
    // Health snapshot adapters
    // ---------------------------------------------------------------------

    internal static void AddHealthChecks(List<DoctorCheck> checks, HealthReport report, bool skipNet)
    {
        // Embedding model schema vs config. ModelMismatch already encodes
        // the rule (schema_model is set AND differs from config_model OR
        // dimensions disagree).
        if (report.Embeddings.ModelMismatch)
        {
            checks.Add(DoctorCheck.Fail("Embedding model",
                $"schema={report.Embeddings.SchemaModel} ({report.Embeddings.SchemaDimensions}d) vs config={report.Embeddings.ConfigModel} ({report.Embeddings.ConfigDimensions}d). The embedder will refuse to start. Run `mailvec switch-model` to migrate the DB to the configured model (rebuilds the vector table and re-queues every message).",
                "pipeline"));
        }
        else if (report.Embeddings.SchemaModel is null)
        {
            checks.Add(DoctorCheck.Warn("Embedding model",
                $"schema not yet stamped (no embeddings written). Will be set to {report.Embeddings.ConfigModel} on first embed.",
                "pipeline"));
        }
        else
        {
            checks.Add(DoctorCheck.Ok("Embedding model",
                $"{report.Embeddings.SchemaModel} ({report.Embeddings.SchemaDimensions}d, matches config)",
                "pipeline"));
        }

        // Coverage: the absolute counts already tell the story. Threshold of
        // 50% for warn is a stake in the ground — high enough that a fresh
        // install with the embedder running shows green within an hour or
        // two; low enough that a stalled embedder mid-archive triggers it.
        var live = Math.Max(report.Database.MessagesTotal - report.Database.MessagesDeleted, 0);
        var coverage = report.Embeddings.CoveragePct;
        var coverageDetail = $"{report.Embeddings.MessagesEmbedded:N0} / {live:N0} messages ({coverage:0.0}%, {report.Embeddings.ChunkCount:N0} chunks)";
        if (live == 0)
        {
            checks.Add(DoctorCheck.Warn("Embedding cover", "no live messages — has the indexer ever run?", "pipeline"));
        }
        else if (coverage >= 95.0)
        {
            checks.Add(DoctorCheck.Ok("Embedding cover", coverageDetail, "pipeline"));
        }
        else if (coverage >= 50.0)
        {
            checks.Add(DoctorCheck.Warn("Embedding cover", coverageDetail + " — embedder is making progress; check back later.", "pipeline"));
        }
        else
        {
            checks.Add(DoctorCheck.Warn("Embedding cover", coverageDetail + " — semantic / hybrid search will miss most of the archive until coverage catches up.", "pipeline"));
        }

        // Last indexed: warn if older than an hour to surface "indexer
        // stopped running" without false-positiving on quiet inboxes.
        if (report.Database.LastIndexedAt is { } at)
        {
            var age = DateTimeOffset.UtcNow - at.ToUniversalTime();
            var detail = $"{at.LocalDateTime:yyyy-MM-dd HH:mm} ({HumanizeAge(age)} ago)";
            checks.Add(age <= TimeSpan.FromHours(1)
                ? DoctorCheck.Ok("Last indexed", detail, "pipeline")
                : DoctorCheck.Warn("Last indexed", detail + " — quiet inbox, or the indexer isn't running.", "pipeline"));
        }
        else
        {
            checks.Add(DoctorCheck.Warn("Last indexed", "no messages indexed yet.", "pipeline"));
        }

        if (skipNet)
        {
            checks.Add(DoctorCheck.Warn("Ollama", "skipped (--no-net)", "pipeline"));
        }
        else if (report.Ollama.Reachable)
        {
            checks.Add(DoctorCheck.Ok("Ollama", $"reachable at {report.Ollama.BaseUrl} (configured model: {report.Ollama.ConfiguredModel})", "pipeline"));
        }
        else if (report.Ollama.EmbeddingModelAvailable == false)
        {
            // The server answered /api/tags — it's up. The embed ping failed
            // because the configured model was never pulled. "Restart Ollama"
            // advice here would send the user chasing a healthy server.
            var hint = IsLocalOllama(report.Ollama.BaseUrl)
                ? $"Run `ollama pull {report.Ollama.ConfiguredModel}`."
                : $"Run `ollama pull {report.Ollama.ConfiguredModel}` on the remote Ollama host.";
            checks.Add(DoctorCheck.Warn("Ollama",
                $"reachable at {report.Ollama.BaseUrl}, but the embedding model {report.Ollama.ConfiguredModel} is not pulled. " +
                $"Embedder is stuck and semantic / hybrid search is degraded; keyword search still works. {hint}",
                "pipeline"));
        }
        else if (report.Ollama.EmbeddingModelAvailable == true)
        {
            // Server up, model listed as pulled, yet the embed probe failed —
            // the model can't actually load. The known cause is the incomplete
            // Homebrew *formula* build (no llama-server, GGML models never
            // load); GPU/memory pressure is the other candidate.
            checks.Add(DoctorCheck.Warn("Ollama",
                $"reachable at {report.Ollama.BaseUrl} and {report.Ollama.ConfiguredModel} is pulled, but the embed probe failed — " +
                "the model can't load. If Ollama came from the Homebrew formula, switch to the cask (`brew install --cask ollama-app`) — " +
                "see the README's Ollama note; otherwise check the host's free memory and the Ollama server log.",
                "pipeline"));
        }
        else
        {
            // Local vs remote get different remediation hints: a loopback URL
            // means the Ollama app/service on THIS machine is down; a remote URL
            // means the network host is down or unreachable (firewall / not
            // listening on 0.0.0.0), where `brew services` on this box is useless.
            var hint = IsLocalOllama(report.Ollama.BaseUrl)
                ? "Check `brew services list | grep ollama` (or that /Applications/Ollama.app is running)."
                : "It's a remote host: confirm it's up, listening on all interfaces (OLLAMA_HOST=0.0.0.0), and reachable through any firewall.";
            checks.Add(DoctorCheck.Warn("Ollama",
                $"unreachable at {report.Ollama.BaseUrl}. Embedder is stuck and semantic / hybrid search is degraded; keyword search still works. {hint}",
                "pipeline"));
        }
    }

    /// <summary>
    /// True when the Ollama base URL points at this machine (loopback), so
    /// remediation hints can differ between a local app and a remote host.
    /// </summary>
    private static bool IsLocalOllama(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return true;
        var h = uri.Host;
        return h is "localhost" or "127.0.0.1" or "::1" or "[::1]";
    }

    /// <summary>
    /// Build a HealthReport without pinging Ollama. Reuses MetadataRepository
    /// and a single COUNT query — same SQL HealthService.ReadCounts uses, so
    /// the offline path produces output indistinguishable from the live path
    /// except for the Ollama line. Cheaper than refactoring HealthService to
    /// take a "skip ping" flag, since this is the only caller that needs the
    /// distinction.
    /// </summary>
    private static HealthReport BuildOfflineHealthReport(ServiceProvider sp, ArchiveOptions archive, OllamaOptions ollama)
    {
        var connections = sp.GetRequiredService<ConnectionFactory>();
        var metadata = sp.GetRequiredService<MetadataRepository>();

        long total, deleted, embedded, chunkCount;
        DateTimeOffset? lastIndexedAt;
        using (var conn = connections.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                  (SELECT COUNT(*) FROM messages),
                  (SELECT COUNT(*) FROM messages WHERE deleted_at IS NOT NULL),
                  (SELECT COUNT(*) FROM messages WHERE embedded_at IS NOT NULL AND deleted_at IS NULL),
                  (SELECT COUNT(*) FROM chunks),
                  (SELECT MAX(indexed_at) FROM messages)
                """;
            using var reader = cmd.ExecuteReader();
            reader.Read();
            total = reader.GetInt64(0);
            deleted = reader.GetInt64(1);
            embedded = reader.GetInt64(2);
            chunkCount = reader.GetInt64(3);
            lastIndexedAt = reader.IsDBNull(4)
                ? null
                : DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture);
        }

        var schemaModel = metadata.Get("embedding_model");
        _ = int.TryParse(metadata.Get("embedding_dimensions"), out var schemaDim);
        bool mismatch = schemaModel is not null && (schemaModel != ollama.EmbeddingModel || (schemaDim != 0 && schemaDim != ollama.EmbeddingDimensions));

        var live = Math.Max(total - deleted, 0);
        var coverage = live == 0 ? 0d : (double)embedded / live;

        return new HealthReport(
            Status: mismatch ? "degraded" : "ok",
            Database: new DatabaseHealth(
                Path: PathExpansion.Expand(archive.DatabasePath),
                MessagesTotal: total,
                MessagesDeleted: deleted,
                LastIndexedAt: lastIndexedAt),
            Embeddings: new EmbeddingHealth(
                SchemaModel: schemaModel,
                SchemaDimensions: schemaDim == 0 ? null : schemaDim,
                ConfigModel: ollama.EmbeddingModel,
                ConfigDimensions: ollama.EmbeddingDimensions,
                ModelMismatch: mismatch,
                MessagesEmbedded: embedded,
                CoveragePct: Math.Round(coverage * 100d, 1),
                ChunkCount: chunkCount),
            Ollama: new OllamaHealth(
                BaseUrl: ollama.BaseUrl,
                Reachable: false,         // honest sentinel; rendered as "skipped"
                ConfiguredModel: ollama.EmbeddingModel),
            // OCR is surfaced by doctor's own dedicated vision-model check, not
            // through this offline report — so a zero/skipped placeholder here.
            Ocr: new OcrHealth(
                Enabled: false,
                VisionModel: ollama.VisionModel,
                ModelAvailable: null,
                Pending: 0,
                Recovered: 0,
                ImagePending: 0,
                ImageRecovered: 0),
            // Doctor's offline-mode HealthReport doesn't have access to the
            // embedder heartbeat metadata; leave the fields blank rather than
            // synthesising values. Doctor's own /health probe (further down)
            // is what actually surfaces a stuck embedder to the user.
            Embedder: new EmbedderHealth(
                LastSuccessAt: null,
                LastFailureAt: null,
                ConsecutiveFailures: 0,
                LastFailureKind: null,
                Stuck: false));
    }

    // ---------------------------------------------------------------------
    // External-process probes
    // ---------------------------------------------------------------------

    /// <summary>
    /// Parse `launchctl list` output once, in one fork+exec, then look up
    /// each label in the resulting table. Cheaper than calling `launchctl
    /// list &lt;label&gt;` per agent (4 forks vs 1) and `launchctl list`
    /// already returns PID + last-exit + label which is everything we need.
    /// </summary>
    private static IReadOnlyDictionary<string, LaunchctlEntry>? TryReadLaunchctlList()
    {
        try
        {
            var psi = new ProcessStartInfo("launchctl", "list")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

            var dict = new Dictionary<string, LaunchctlEntry>(StringComparer.Ordinal);
            // Format: "PID\tStatus\tLabel" with a header row "PID\tStatus\tLabel".
            // PID = "-" when not running.
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                if (parts[0] == "PID") continue;
                int? pid = int.TryParse(parts[0], out var p) ? p : null;
                int? lastExit = int.TryParse(parts[1], out var e) ? e : null;
                var label = parts[2].Trim();
                if (label.Length == 0) continue;
                dict[label] = new LaunchctlEntry(pid, lastExit, label);
            }
            return dict;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reports the most recent error written to
    /// <c>~/Library/Logs/Mailvec/mailvec-mbsync.err.log</c>, if any fall
    /// inside the freshness window. Uses the same MbsyncErrorTail the
    /// tray reads from so the doctor checklist and the tray's mbsync tile
    /// can't disagree about what's wrong.
    /// </summary>
    private static DoctorCheck InspectMbsyncStderr()
    {
        var err = new Mailvec.Core.Tray.MbsyncErrorTail().CheckRecent();
        if (err is null)
        {
            return DoctorCheck.Ok("mbsync stderr", "no recent errors", "services");
        }

        var hint = err.Kind switch
        {
            Mailvec.Core.Tray.MbsyncErrorKind.Locked  => " Remove the stale .mbsyncstate.lock and kickstart com.mailvec.mbsync.",
            Mailvec.Core.Tray.MbsyncErrorKind.Dns     => " Check network connectivity / DNS — usually transient.",
            Mailvec.Core.Tray.MbsyncErrorKind.Network => " Usually transient; will retry on next schedule.",
            Mailvec.Core.Tray.MbsyncErrorKind.Auth    => " Rotate the Fastmail / IMAP app password and update the keychain entry.",
            _                                          => string.Empty,
        };
        var detail = $"recent error: {Truncate(err.Message, 120)}.{hint} Full log: ~/Library/Logs/Mailvec/mailvec-mbsync.err.log";

        // Lock is escalated because every subsequent scheduled run will
        // also fail until the lock is cleared. The rest are usually
        // transient — flag them so they're visible but don't fail the
        // overall check.
        return err.Kind == Mailvec.Core.Tray.MbsyncErrorKind.Locked
            ? DoctorCheck.Fail("mbsync stderr", detail, "services")
            : DoctorCheck.Warn("mbsync stderr", detail, "services");
    }

    private static DoctorCheck InspectLaunchd(string label, IReadOnlyDictionary<string, LaunchctlEntry>? listing)
    {
        if (listing is null)
        {
            return DoctorCheck.Warn(label, "could not run `launchctl list` — is this macOS?", "services");
        }
        if (!listing.TryGetValue(label, out var entry))
        {
            return DoctorCheck.Warn(label,
                $"agent not loaded. Run ops/install.sh to bootstrap it, or skip this if you don't run Mailvec under launchd.",
                "services");
        }
        // mbsync is a one-shot scheduled agent — a missing PID is normal
        // (it runs every 5 minutes and exits). Other agents are long-running
        // BackgroundServices / web hosts; missing PID means the process
        // crashed and launchd hasn't restarted it.
        var isOneShot = label.EndsWith(".mbsync", StringComparison.Ordinal);
        if (entry.Pid is { } pid)
        {
            return DoctorCheck.Ok(label, $"running (pid {pid})", "services");
        }
        if (isOneShot)
        {
            var exitDetail = entry.LastExit is { } code ? $" (last exit: {code})" : string.Empty;
            return DoctorCheck.Ok(label, "loaded — runs on schedule" + exitDetail, "services");
        }
        var exitNote = entry.LastExit is { } x && x != 0 ? $" — last exit code {x}" : string.Empty;
        return DoctorCheck.Warn(label, "loaded but not running" + exitNote, "services");
    }

    private static async Task<DoctorCheck> ProbeMcpHealthAsync(string url, CancellationToken ct)
    {
        // 10s timeout. The /health endpoint legitimately takes 4–6s on the
        // first call after a redeploy: Ollama ping (up to 2s internal cap),
        // several SQL aggregates against a multi-GB archive, and .NET JIT
        // for the endpoint code path on cold start. A 3s ceiling was
        // flagging every post-redeploy doctor run as "no response" even
        // though the server was healthy — see the comment trail in
        // CLAUDE.md → "MCP API stability" if you're tempted to shrink it.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            if ((int)response.StatusCode == 200)
            {
                return DoctorCheck.Ok("MCP /health", $"200 ok ({url})", "mcp");
            }
            if ((int)response.StatusCode == 503)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return DoctorCheck.Warn("MCP /health", $"503 degraded — {Truncate(body, 200)}", "mcp");
            }
            return DoctorCheck.Warn("MCP /health", $"unexpected status {(int)response.StatusCode} ({url})", "mcp");
        }
        catch (HttpRequestException)
        {
            return DoctorCheck.Warn("MCP /health",
                $"unreachable at {url}. The launchd agent might not be loaded — run `launchctl print gui/$UID/com.mailvec.mcp` or `ops/install.sh`.",
                "mcp");
        }
        catch (TaskCanceledException)
        {
            return DoctorCheck.Warn("MCP /health", $"no response within 10s ({url}).", "mcp");
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static string? ResolveExtensionPath(string configured)
    {
        if (Path.IsPathRooted(configured) && File.Exists(configured)) return configured;
        if (File.Exists(configured)) return Path.GetFullPath(configured);

        var beside = Path.Combine(AppContext.BaseDirectory, configured);
        if (File.Exists(beside)) return Path.GetFullPath(beside);

        // The repo root resolution mirrors how Directory.Build.props copies
        // the dylib into each project's bin output, so a from-source
        // `dotnet run` against the working tree resolves correctly even
        // when the configured path is a relative ./runtimes/... string.
        var fromRepo = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", configured);
        return File.Exists(fromRepo) ? Path.GetFullPath(fromRepo) : null;
    }

    private static string? ResolveOnPath(string command)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, command);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
    }

    internal static string HumanizeAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60) return $"{age.TotalSeconds:0}s";
        if (age.TotalMinutes < 60) return $"{age.TotalMinutes:0}m";
        if (age.TotalHours < 48) return $"{age.TotalHours:0}h";
        return $"{age.TotalDays:0}d";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    internal static (int Ok, int Warn, int Fail) Summarize(IReadOnlyList<DoctorCheck> checks)
    {
        int ok = 0, warn = 0, fail = 0;
        foreach (var c in checks)
        {
            switch (c.Status) { case "ok": ok++; break; case "warn": warn++; break; case "fail": fail++; break; }
        }
        return (ok, warn, fail);
    }

    private static void PrintHumanReport(List<DoctorCheck> checks)
    {
        Console.WriteLine();
        Console.WriteLine("Mailvec preflight");
        Console.WriteLine(new string('=', 17));

        // Group by section while preserving insertion order so the output
        // reads top-to-bottom in the order checks ran (config -> pipeline
        // -> services -> tools -> mcp).
        var sectionOrder = new List<string>();
        var bySection = new Dictionary<string, List<DoctorCheck>>(StringComparer.Ordinal);
        foreach (var c in checks)
        {
            if (!bySection.ContainsKey(c.Section))
            {
                sectionOrder.Add(c.Section);
                bySection[c.Section] = new List<DoctorCheck>();
            }
            bySection[c.Section].Add(c);
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["config"] = "Configuration",
            ["pipeline"] = "Pipeline",
            ["services"] = "Services (launchd)",
            ["tools"] = "External tools",
            ["mcp"] = "MCP",
        };

        // Width of the name column tuned to the longest check name across
        // all sections so columns line up cleanly. +2 keeps a gap before
        // the detail.
        int width = checks.Max(c => c.Name.Length) + 2;
        foreach (var section in sectionOrder)
        {
            Console.WriteLine();
            Console.WriteLine(labels.GetValueOrDefault(section, section));
            foreach (var c in bySection[section])
            {
                var glyph = c.Status switch { "ok" => "✓", "warn" => "⚠", _ => "✗" };
                Console.WriteLine($"  {glyph} {c.Name.PadRight(width)} {c.Detail}");
            }
        }

        var (ok, warn, fail) = Summarize(checks);
        Console.WriteLine();
        Console.WriteLine($"{ok} pass, {warn} warn, {fail} fail");
        if (fail > 0)
        {
            Console.WriteLine("Some checks failed — Mailvec may not work end-to-end. Address fail items first.");
        }
        else if (warn > 0)
        {
            Console.WriteLine("All required checks passed; review warnings to confirm they're intentional.");
        }
        else
        {
            Console.WriteLine("All clear.");
        }
    }

    // ---------------------------------------------------------------------
    // Records
    // ---------------------------------------------------------------------

    internal sealed record DoctorCheck(string Name, string Status, string Detail, string Section)
    {
        public static DoctorCheck Ok(string name, string detail, string section) => new(name, "ok", detail, section);
        public static DoctorCheck Warn(string name, string detail, string section) => new(name, "warn", detail, section);
        public static DoctorCheck Fail(string name, string detail, string section) => new(name, "fail", detail, section);
    }

    private sealed record LaunchctlEntry(int? Pid, int? LastExit, string Label);
}
