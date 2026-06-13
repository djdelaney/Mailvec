using System.Diagnostics;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Health;
using Mailvec.Core.Options;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Tray;

/// <summary>
/// Reads everything the Preferences window needs in one shot. All fields are
/// derived from existing config + a few SQLite/launchd queries — nothing here
/// requires new schema or new IPC. Returned values are display-ready strings
/// where the Swift side would otherwise format them inconsistently across
/// tabs (paths, byte sizes, "running" labels).
/// </summary>
public sealed class TraySystemService(
    HealthService health,
    LaunchdInspector launchd,
    ConnectionFactory connections,
    MetadataRepository metadata,
    IEmbeddingClient ollama,
    IOptions<ArchiveOptions> archiveOpts,
    IOptions<IngestOptions> ingestOpts,
    IOptions<OllamaOptions> ollamaOpts,
    IOptions<McpOptions> mcpOpts)
{
    public async Task<TraySystem> BuildAsync(CancellationToken ct = default)
    {
        var healthReport = await health.CheckAsync(ct).ConfigureAwait(false);
        var launchdMap = await launchd.InspectAllAsync(ct).ConfigureAwait(false);

        var mbsync = launchdMap.GetValueOrDefault("com.mailvec.mbsync");
        var mcp = launchdMap.GetValueOrDefault("com.mailvec.mcp");

        var dbPath = PathExpansion.Expand(archiveOpts.Value.DatabasePath);
        var dbBytes = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;
        var schemaVersion = metadata.Get("schema_version") ?? "unknown";

        var (ollamaReachable, ollamaPingMs) = await PingOllamaAsync(ct).ConfigureAwait(false);

        var soft = ReadSoftDeletedCount();
        var maildir = PathExpansion.Expand(ingestOpts.Value.MaildirRoot);

        var mbsyncrc = ResolveMbsyncrcPath();
        // Source-of-truth for these three is *outside* the .NET process —
        // mbsync's own config file for host/user and the launchd plist for
        // the timer. We parse them lazily here so the tray sees real values
        // and the Sync prefs tab can't lie. Both parsers are forgiving:
        // returns nulls when the file is missing or malformed, and
        // downstream code already handles the placeholder fallbacks.
        var (imapHost, imapUser) = ParseMbsyncrc(mbsyncrc);
        var schedule = ReadMbsyncSchedule();

        return new TraySystem(
            MaildirRoot: maildir,
            MbsyncrcPath: mbsyncrc,
            MbsyncSchedule: schedule,
            ImapHost: imapHost ?? "(see ~/.mbsyncrc)",
            ImapUser: imapUser ?? "(see ~/.mbsyncrc)",
            LastSyncRelative: mbsync is not null ? Relative(mbsync) : null,
            LastSyncDetail: mbsync is not null ? Detail(mbsync) : null,
            NextSyncRelative: mbsync is not null ? $"in ≤ {schedule}" : null,

            DbPath: dbPath,
            DbSize: ByteString(dbBytes),
            SchemaVersion: schemaVersion,
            VecDylibVersion: TryReadVecVersion(),

            OllamaEndpoint: ollamaOpts.Value.BaseUrl,
            OllamaReachable: ollamaReachable,
            OllamaPingMs: ollamaPingMs,
            EmbeddingModel: ollamaOpts.Value.EmbeddingModel,
            ModelDimensions: ollamaOpts.Value.EmbeddingDimensions,
            SchemaModelMatches: !healthReport.Embeddings.ModelMismatch,
            CoverageDone: healthReport.Embeddings.MessagesEmbedded,
            CoverageTotal: healthReport.Database.MessagesTotal - healthReport.Database.MessagesDeleted,

            McpHttpEnabled: mcp is not null && mcp.State == "running",
            McpBindAddress: mcpOpts.Value.BindAddress,
            McpPort: mcpOpts.Value.Port,
            McpbInstalled: McpbInstalled(out var mcpbVersion),
            McpbVersion: mcpbVersion,
            AttachmentDownloadDir: PathExpansion.Expand(mcpOpts.Value.AttachmentDownloadDir),

            SoftDeletedCount: soft);
    }

    private async Task<(bool Reachable, int LatencyMs)> PingOllamaAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var ok = await ollama.PingAsync(ct).ConfigureAwait(false);
        sw.Stop();
        return (ok, (int)sw.ElapsedMilliseconds);
    }

    private long ReadSoftDeletedCount()
    {
        try
        {
            using var conn = connections.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE deleted_at IS NOT NULL";
            return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    internal static string Relative(LaunchdServiceInfo info)
    {
        // We don't have a true last-completion timestamp; the closest signal is
        // "service is loaded with N runs". Display Runs so the user has *some*
        // accountability number; the dashboard's mbsync tile already conveys
        // the precise running/idle state.
        if (info.State == "running") return "now";
        if (info.Runs == 0) return "never";
        return $"after {info.Runs} runs";
    }

    internal static string Detail(LaunchdServiceInfo info)
    {
        if (info.State == "running") return "mbsync currently syncing";
        var exit = info.LastExitCode is { } ec ? $"last exit {ec}" : "no exit recorded";
        return $"{exit} · {info.Runs} runs since load";
    }

    internal static string ByteString(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F1} KB";
        return $"{bytes} B";
    }

    /// <summary>
    /// Reads `v<version>` from a VERSION sidecar that ops/fetch-sqlite-vec.sh
    /// writes alongside vec0.dylib. The dylib itself doesn't expose a build
    /// string we can introspect (it's loaded via SQLite's extension API
    /// before any version-reporting function exists), so we cooperate at
    /// install time and persist the version we downloaded.
    /// </summary>
    private string TryReadVecVersion()
    {
        try
        {
            var configured = archiveOpts.Value.SqliteVecExtensionPath ?? string.Empty;
            // The configured path is `./runtimes/<rid>/native/vec0.dylib`
            // relative to the binary. Look for a sibling VERSION file —
            // resolve relative to AppContext.BaseDirectory like
            // ConnectionFactory does.
            var dylibDir = Path.GetDirectoryName(configured) ?? string.Empty;
            var rel = string.IsNullOrEmpty(dylibDir) ? "VERSION" : Path.Combine(dylibDir, "VERSION");
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, rel),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", rel),
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c))
                {
                    var v = File.ReadAllText(c).Trim();
                    if (!string.IsNullOrEmpty(v)) return v.StartsWith('v') ? v : "v" + v;
                }
            }
        }
        catch { /* best effort */ }
        return "—";
    }

    private static string ResolveMbsyncrcPath()
    {
        var candidate = PathExpansion.Expand("~/.mbsyncrc");
        return candidate;
    }

    /// <summary>
    /// Pulls the first `Host` and `User` directives out of an mbsync config
    /// file. mbsync's syntax is `Directive Value` (whitespace-separated,
    /// directives are case-insensitive but conventionally MixedCase) with
    /// `#` line comments. Multiple IMAPAccount blocks would each have their
    /// own — we surface the first.
    /// </summary>
    internal static (string? Host, string? User) ParseMbsyncrc(string path)
    {
        try
        {
            if (!File.Exists(path)) return (null, null);
            string? host = null, user = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var space = line.IndexOfAny([' ', '\t']);
                if (space < 0) continue;
                var directive = line[..space];
                var value = line[(space + 1)..].Trim();
                if (host is null && string.Equals(directive, "Host", StringComparison.OrdinalIgnoreCase))
                    host = value;
                else if (user is null && string.Equals(directive, "User", StringComparison.OrdinalIgnoreCase))
                    user = value;
                if (host is not null && user is not null) break;
            }
            return (host, user);
        }
        catch { return (null, null); }
    }

    /// <summary>
    /// Reads `StartInterval` (seconds) out of the installed mbsync launchd
    /// plist and renders it as a human-friendly string ("5 minutes",
    /// "10 minutes", "1 hour"). Falls back to the literal seconds value or
    /// "unknown" if the file can't be parsed.
    ///
    /// We use a regex on the XML rather than NSPropertyListSerialization —
    /// no plist library ships with .NET and the format is stable enough
    /// that this is cheaper than spawning `plutil`.
    /// </summary>
    private static string ReadMbsyncSchedule()
    {
        try
        {
            var path = PathExpansion.Expand("~/Library/LaunchAgents/com.mailvec.mbsync.plist");
            if (!File.Exists(path)) return "unknown";
            var xml = File.ReadAllText(path);
            var m = System.Text.RegularExpressions.Regex.Match(
                xml,
                @"<key>\s*StartInterval\s*</key>\s*<integer>\s*(\d+)\s*</integer>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var seconds)) return "unknown";
            return FormatSchedule(seconds);
        }
        catch
        {
            return "unknown";
        }
    }

    internal static string FormatSchedule(int seconds)
    {
        if (seconds <= 0) return "unknown";
        if (seconds % 3600 == 0)
        {
            var h = seconds / 3600;
            return h == 1 ? "1 hour" : $"{h} hours";
        }
        if (seconds % 60 == 0)
        {
            var m = seconds / 60;
            return m == 1 ? "1 minute" : $"{m} minutes";
        }
        return $"{seconds} seconds";
    }

    private bool McpbInstalled(out string? version)
    {
        // Claude Desktop renamed the install dir from "Connectors" to
        // "Claude Extensions" somewhere along the line — we check both so
        // older installs still report correctly, with "Claude Extensions"
        // tried first since that's what current builds use. We don't know
        // the bundle's directory id ahead of time (it's
        // local.mcpb.<author>.<name> for sideloaded, ant.dir.* for store-
        // distributed), so we walk the directory and match manifest.json
        // by its `name` field.
        version = null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidateDirs = new[]
        {
            Path.Combine(home, "Library", "Application Support", "Claude", "Claude Extensions"),
            Path.Combine(home, "Library", "Application Support", "Claude", "Connectors"),
        };
        try
        {
            foreach (var root in candidateDirs)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var manifest = Path.Combine(dir, "manifest.json");
                    if (!File.Exists(manifest)) continue;
                    var json = File.ReadAllText(manifest);
                    if (!json.Contains("\"name\": \"mailvec\"", StringComparison.Ordinal) &&
                        !json.Contains("\"name\":\"mailvec\"", StringComparison.Ordinal)) continue;
                    version = ExtractJsonString(json, "version");
                    return true;
                }
            }
        }
        catch
        {
            // best-effort
        }
        return false;
    }

    internal static string? ExtractJsonString(string json, string key)
    {
        var needle = $"\"{key}\"";
        var i = json.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return null;
        i = json.IndexOf('"', i + needle.Length);
        if (i < 0) return null;
        var end = json.IndexOf('"', i + 1);
        return end < 0 ? null : json[(i + 1)..end];
    }
}
