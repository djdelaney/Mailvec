using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Microsoft.Data.Sqlite;

namespace Mailvec.Core.Tests.Data;

/// <summary>
/// Covers ConnectionFactory's own behavior — specifically that it locks down the
/// archive (the richest PII object in the system) to owner-only permissions, so
/// its confidentiality doesn't rely on $HOME being 0700 (macOS-only convention).
/// </summary>
public sealed class ConnectionFactoryTests : IDisposable
{
    private const UnixFileMode OwnerRw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerRwx = OwnerRw | UnixFileMode.UserExecute;

    private readonly string _dir;
    private readonly string _dbPath;

    public ConnectionFactoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mailvec-connfactory-tests-" + Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_dir, "archive.sqlite");
    }

    private ConnectionFactory NewFactory() =>
        new(Microsoft.Extensions.Options.Options.Create(new ArchiveOptions { DatabasePath = _dbPath }));

    [Fact]
    public void Open_restricts_db_dir_and_sidecars_to_owner_only()
    {
        // POSIX file modes don't exist on Windows; SetUnixFileMode is a no-op there.
        if (OperatingSystem.IsWindows()) return;

        var factory = NewFactory();
        using (var conn = factory.Open())
        {
            // A committed write guarantees the -wal/-shm sidecars are present and
            // uncheckpointed while the connection is open, so we can assert on them.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t(x INTEGER); INSERT INTO t VALUES (1);";
            cmd.ExecuteNonQuery();

            File.GetUnixFileMode(_dir).ShouldBe(OwnerRwx);
            File.GetUnixFileMode(_dbPath).ShouldBe(OwnerRw);
            File.GetUnixFileMode(_dbPath + "-wal").ShouldBe(OwnerRw);
            File.GetUnixFileMode(_dbPath + "-shm").ShouldBe(OwnerRw);
        }
    }

    [Fact]
    public void Open_self_heals_loosened_db_permissions()
    {
        if (OperatingSystem.IsWindows()) return;

        // First open hardens a fresh DB to 0600.
        using (NewFactory().Open()) { }

        // Simulate a DB created before this hardening landed: world-readable.
        var loose = UnixFileMode.UserRead | UnixFileMode.UserWrite
                  | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(_dbPath, loose);
        File.GetUnixFileMode(_dbPath).ShouldBe(loose);

        // A fresh factory (as on a process restart) re-hardens on next open —
        // the fix is self-healing, not just applied at create time.
        using (NewFactory().Open()) { }

        File.GetUnixFileMode(_dbPath).ShouldBe(OwnerRw);
    }

    [Fact]
    public void Open_applies_every_promised_pragma_for_real()
    {
        // Regression guard for the silent-pragma-no-op class that already bit
        // this repo once: Microsoft.Data.Sqlite's ExecuteNonQuery drops
        // result-returning pragmas, so `journal_mode = WAL` in the schema
        // script never took effect and DBs shipped in rollback-journal mode
        // for months. Read every pragma ConnectionFactory promises back on a
        // fresh connection so a future refactor that silently no-ops one
        // fails here instead of in production behavior.
        using var conn = NewFactory().Open();

        Scalar(conn, "PRAGMA journal_mode;").ShouldBe("wal");
        Scalar(conn, "PRAGMA busy_timeout;").ShouldBe(5000L);
        Scalar(conn, "PRAGMA foreign_keys;").ShouldBe(1L);
    }

    [Fact]
    public void Open_caps_wal_growth_with_journal_size_limit()
    {
        // Without a journal_size_limit, SQLite reuses space inside the WAL but
        // never shrinks the file — one giant transaction (reindex /
        // switch-model clearing the vector table) sets a multi-GB high-water
        // mark that persists forever. The limit lets checkpoints truncate it.
        using var conn = NewFactory().Open();

        Scalar(conn, "PRAGMA journal_size_limit;").ShouldBe(67108864L); // 64 MiB
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose()
    {
        // Nothing was created on the skipped-Windows path.
        if (!Directory.Exists(_dir)) return;

        // SQLite holds the file open until this DataSource's pool is cleared;
        // scope the clear to this DB (mirrors TempDatabase) rather than globally.
        try
        {
            using var conn = NewFactory().Open();
            SqliteConnection.ClearPool(conn);
        }
        catch (SqliteException) { /* best effort */ }

        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort cleanup */ }
    }
}
