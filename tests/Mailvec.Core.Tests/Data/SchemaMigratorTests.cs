using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Tests.Data;

public class SchemaMigratorTests
{
    [Fact]
    public void Creates_all_expected_tables_on_fresh_database()
    {
        using var db = new TempDatabase();
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";

        var tables = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) tables.Add(reader.GetString(0));

        tables.ShouldContain("messages");
        tables.ShouldContain("chunks");
        tables.ShouldContain("sync_state");
        tables.ShouldContain("metadata");
        tables.ShouldContain("messages_fts");
        tables.ShouldContain("chunk_embeddings");
    }

    [Fact]
    public void Fresh_database_lands_at_latest_schema_version()
    {
        using var db = new TempDatabase();
        ReadSchemaVersion(db).ShouldBe(3);
    }

    [Fact]
    public void Fresh_database_has_messages_content_hash_column()
    {
        using var db = new TempDatabase();
        TableHasColumn(db, "messages", "content_hash").ShouldBeTrue();
    }

    [Fact]
    public void Migrator_brings_v2_database_forward_to_latest()
    {
        // Simulate an existing DB created under schema v2 (no content_hash
        // column). Run the migrator against it and confirm:
        //   1. content_hash column gets added
        //   2. schema_version is bumped to 3
        //   3. existing rows survive (column is NULL on them)
        var dir = Path.Combine(Path.GetTempPath(), "mailvec-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "archive.sqlite");
        try
        {
            var connections = new ConnectionFactory(Microsoft.Extensions.Options.Options.Create(new ArchiveOptions { DatabasePath = dbPath }));

            using (var seed = connections.Open())
            {
                ApplyV2Schema(seed);
                InsertSampleMessage(seed, messageId: "legacy@x", subject: "before-migration");
            }

            // Sanity: pre-migration state is what we expect.
            TableHasColumn(connections, "messages", "content_hash").ShouldBeFalse();
            ReadSchemaVersion(connections).ShouldBe(2);

            // Run the migrator.
            new SchemaMigrator(connections, NullLogger<SchemaMigrator>.Instance).EnsureUpToDate();

            // Post-migration: column exists, schema bumped, prior row preserved.
            TableHasColumn(connections, "messages", "content_hash").ShouldBeTrue();
            ReadSchemaVersion(connections).ShouldBe(3);

            using var verify = connections.Open();
            using var cmd = verify.CreateCommand();
            cmd.CommandText = "SELECT subject, content_hash FROM messages WHERE message_id = 'legacy@x'";
            using var r = cmd.ExecuteReader();
            r.Read().ShouldBeTrue();
            r.GetString(0).ShouldBe("before-migration");
            r.IsDBNull(1).ShouldBeTrue();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void Migrator_is_idempotent_when_already_at_latest()
    {
        using var db = new TempDatabase();
        // Second call should be a no-op and not throw.
        new SchemaMigrator(db.Connections, NullLogger<SchemaMigrator>.Instance).EnsureUpToDate();
        ReadSchemaVersion(db).ShouldBe(3);
    }

    /// <summary>
    /// Apply only the v2 portion of the initial schema — what an existing
    /// DB created before the v3 migration would look like. Hand-rolled
    /// because we don't keep historical schema scripts in tree.
    /// </summary>
    private static void ApplyV2Schema(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        var statements = new[]
        {
            "PRAGMA journal_mode = WAL",
            "PRAGMA foreign_keys = ON",
            // messages — v2 layout (no content_hash column)
            """
            CREATE TABLE messages (
                id INTEGER PRIMARY KEY,
                message_id TEXT UNIQUE NOT NULL,
                thread_id TEXT,
                maildir_path TEXT NOT NULL,
                maildir_filename TEXT NOT NULL,
                folder TEXT NOT NULL,
                subject TEXT,
                from_address TEXT,
                from_name TEXT,
                to_addresses TEXT,
                cc_addresses TEXT,
                date_sent TEXT,
                date_received TEXT,
                size_bytes INTEGER,
                has_attachments INTEGER DEFAULT 0,
                attachment_names TEXT,
                body_text TEXT,
                body_html TEXT,
                raw_headers TEXT,
                indexed_at TEXT NOT NULL,
                embedded_at TEXT,
                deleted_at TEXT
            )
            """,
            "CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL)",
            "INSERT INTO metadata(key, value) VALUES ('schema_version', '2')",
        };
        foreach (var sql in statements)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertSampleMessage(Microsoft.Data.Sqlite.SqliteConnection conn, string messageId, string subject)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (message_id, maildir_path, maildir_filename, folder, subject, indexed_at)
            VALUES ($mid, 'INBOX/cur', 'f1', 'INBOX', $subj, '2025-01-01T00:00:00.0000000+00:00')
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);
        cmd.Parameters.AddWithValue("$subj", subject);
        cmd.ExecuteNonQuery();
    }

    private static int ReadSchemaVersion(TempDatabase db) => ReadSchemaVersion(db.Connections);

    private static int ReadSchemaVersion(ConnectionFactory connections)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version'";
        return int.Parse((string)cmd.ExecuteScalar()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TableHasColumn(TempDatabase db, string table, string column) => TableHasColumn(db.Connections, table, column);

    private static bool TableHasColumn(ConnectionFactory connections, string table, string column)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $col";
        cmd.Parameters.AddWithValue("$col", column);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }
}
