using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mailvec.Core.Options;

namespace Mailvec.Core.Data;

public sealed class ConnectionFactory
{
    private string? _connectionString;
    private readonly string _dbPath;
    private readonly string? _vecExtensionPath;
    private readonly ILogger<ConnectionFactory>? _logger;
    private bool _permsHardened;

    // Test seam: Microsoft.Data.Sqlite retries a busy statement until the
    // command timeout, so contention tests against the production 30s would
    // take 30s each. Init-only — production always runs the default. The
    // connection string is built lazily (below) because init properties are
    // assigned after the constructor runs.
    internal int DefaultTimeoutSeconds { get; init; } = 30;

    private string ConnectionString => _connectionString ??= new SqliteConnectionStringBuilder
    {
        DataSource = _dbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Default,
        DefaultTimeout = DefaultTimeoutSeconds,
    }.ToString();

    public ConnectionFactory(IOptions<ArchiveOptions> options, ILogger<ConnectionFactory>? logger = null)
    {
        _logger = logger;
        var dbPath = PathExpansion.Expand(options.Value.DatabasePath);
        _dbPath = dbPath;
        var dbDir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dbDir);
        // The archive holds full mail bodies, subjects, addresses and attachment
        // text — the single richest PII object in the system. Lock its directory
        // to owner-only (0700) so its confidentiality doesn't rely on $HOME being
        // 0700 (true on macOS, NOT guaranteed on Linux, and irrelevant for a
        // container bind-mount whose perms come from the host).
        HardenPath(dbDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, "archive directory");

        _configuredVecPath = options.Value.SqliteVecExtensionPath;
        _vecExtensionPath = ResolveVecExtension(_configuredVecPath);
    }

    private readonly string _configuredVecPath;

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        if (_vecExtensionPath is not null)
        {
            conn.EnableExtensions(true);
            conn.LoadExtension(_vecExtensionPath);
        }
        else if (!string.IsNullOrWhiteSpace(_configuredVecPath))
        {
            // A path was configured but nothing resolved. Proceeding used to
            // "work" until the first vec0-touching statement — which is the
            // very next thing on a fresh DB (001_initial.sql declares a vec0
            // virtual table) — and every CLI command except doctor then died
            // with a raw `SQLite Error 1: 'no such module: vec0'` stack trace
            // naming neither the missing file nor the fix.
            conn.Dispose();
            throw new InvalidOperationException(
                $"sqlite-vec extension not found (Archive:SqliteVecExtensionPath = '{_configuredVecPath}', " +
                "no candidate resolved against the binary or repo directories). " +
                "Run ops/fetch-sqlite-vec.sh from the repo root, then retry.");
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
                """;
            cmd.ExecuteNonQuery();
        }

        // PRAGMA journal_mode = WAL must be run via a result-reading API —
        // ExecuteNonQuery silently no-ops it under Microsoft.Data.Sqlite, which
        // had us shipping in DELETE mode despite 001_initial.sql's intent.
        // Setting it per-connection is cheap (no-op once the file is in WAL)
        // and self-heals any DB that was created before this fix landed.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode = WAL;";
            _ = cmd.ExecuteScalar();
        }

        // Cap how large the -wal file may REMAIN after a checkpoint. SQLite's
        // autocheckpoint reuses space inside the WAL but never shrinks the
        // file, so one giant transaction (reindex / switch-model clearing the
        // ~1.2 GB vector table) sets a multi-GB high-water mark that persists
        // forever — the observed ~2 GB WAL in search-performance.md. With the
        // limit set, the next checkpoint truncates the file back down.
        // Result-returning pragma — same ExecuteScalar requirement as
        // journal_mode above (ExecuteNonQuery silently no-ops it).
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_size_limit = 67108864;"; // 64 MiB
            _ = cmd.ExecuteScalar();
        }

        // The file exists now (ReadWriteCreate) and the WAL pragma has created the
        // -wal/-shm sidecars, so this is the first safe point to restrict all three
        // to 0600. Done once (idempotent, cheap); self-heals a DB created before
        // this landed. SetUnixFileMode on the main file doesn't cover the sidecars,
        // so we chmod each explicitly.
        HardenPermissions();

        return conn;
    }

    private void HardenPermissions()
    {
        if (_permsHardened) return;
        const UnixFileMode ownerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        HardenPath(_dbPath, ownerReadWrite, "archive DB");
        HardenPath(_dbPath + "-wal", ownerReadWrite, "archive WAL");
        HardenPath(_dbPath + "-shm", ownerReadWrite, "archive SHM");
        _permsHardened = true;
    }

    /// <summary>
    /// Best-effort restriction of a file/dir to the given owner-only mode. A no-op
    /// on Windows (SetUnixFileMode throws PlatformNotSupportedException there) and
    /// on a missing path. A chmod failure never propagates — some container bind
    /// mounts and network filesystems don't honor POSIX modes; hardening the DB
    /// must not be able to crash the connection-open path. We warn once so a silent
    /// non-restriction on Linux/Docker is at least visible in the logs.
    /// </summary>
    private void HardenPath(string path, UnixFileMode mode, string label)
    {
        if (OperatingSystem.IsWindows()) return;
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger?.LogWarning(ex,
                "Could not restrict permissions on the {Label} at {Path}; it may be readable by " +
                "other local accounts. Expected on filesystems without POSIX modes (some container " +
                "bind mounts / network volumes) — set the mount's ownership/mode at the host instead.",
                label, path);
        }
    }

    /// <summary>
    /// Resolves the configured path against well-known locations so a relative
    /// path like "./runtimes/osx-arm64/native/vec0.dylib" works whether the
    /// process is run via `dotnet run`, from a test runner, or as a published exe.
    /// Returns null if the dylib cannot be found, in which case extension
    /// loading is skipped (callers that need vec0 will fail at query time).
    /// </summary>
    private static string? ResolveVecExtension(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;

        var expanded = configured.StartsWith('~') ? PathExpansion.Expand(configured) : configured;

        if (Path.IsPathRooted(expanded) && File.Exists(expanded)) return expanded;

        // Relative path: try AppContext.BaseDirectory (next to the running .dll),
        // then walk upwards to a checked-in repo copy.
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, expanded)),
        };

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            candidates.Add(Path.Combine(dir.FullName, expanded.TrimStart('.', Path.DirectorySeparatorChar, '/')));
            dir = dir.Parent;
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
