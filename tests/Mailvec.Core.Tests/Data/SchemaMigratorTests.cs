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
        ReadSchemaVersion(db).ShouldBe(SchemaMigrator.LatestSchemaVersion);
    }

    [Fact]
    public void Fresh_database_has_messages_content_hash_column()
    {
        using var db = new TempDatabase();
        TableHasColumn(db, "messages", "content_hash").ShouldBeTrue();
    }

    [Fact]
    public void SubstituteEmbeddingConfig_replaces_all_three_tokens()
    {
        var sql = LoadInitialSchemaSql();

        var result = SchemaMigrator.SubstituteEmbeddingConfig(sql, "qwen3-embedding:4b", 2560);

        result.ShouldContain("FLOAT[2560]");
        result.ShouldContain("'qwen3-embedding:4b'");
        result.ShouldContain("('embedding_dimensions', '2560')");
        result.ShouldNotContain("FLOAT[1024]");
        result.ShouldNotContain("'mxbai-embed-large'");
    }

    [Fact]
    public void SubstituteEmbeddingConfig_throws_when_token_missing()
    {
        var doctored = LoadInitialSchemaSql().Replace("FLOAT[1024]", "FLOAT[999]", StringComparison.Ordinal);

        var ex = Should.Throw<InvalidOperationException>(
            () => SchemaMigrator.SubstituteEmbeddingConfig(doctored, "qwen3-embedding:4b", 2560));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public void SubstituteEmbeddingConfig_throws_when_token_duplicated()
    {
        var doctored = LoadInitialSchemaSql() + "\n-- stray duplicate: FLOAT[1024]";

        var ex = Should.Throw<InvalidOperationException>(
            () => SchemaMigrator.SubstituteEmbeddingConfig(doctored, "qwen3-embedding:4b", 2560));
        ex.Message.ShouldContain("more than once");
    }

    [Fact]
    public void SubstituteEmbeddingConfig_rejects_quote_in_model_name()
    {
        Should.Throw<ArgumentException>(
            () => SchemaMigrator.SubstituteEmbeddingConfig(LoadInitialSchemaSql(), "evil'model", 1024));
    }

    [Fact]
    public void Fresh_database_uses_configured_model_and_dimensions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mailvec-dims-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var connections = new ConnectionFactory(Microsoft.Extensions.Options.Options.Create(
            new ArchiveOptions { DatabasePath = Path.Combine(dir, "archive.sqlite") }));
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(
                new OllamaOptions { EmbeddingModel = "qwen3-embedding:4b", EmbeddingDimensions = 2560 });

            new SchemaMigrator(connections, NullLogger<SchemaMigrator>.Instance, opts).EnsureUpToDate();

            ReadMetadata(connections, "embedding_model").ShouldBe("qwen3-embedding:4b");
            ReadMetadata(connections, "embedding_dimensions").ShouldBe("2560");
            VectorTableSql(connections).ShouldContain("FLOAT[2560]");
            InsertVector(connections, chunkId: 1, new float[2560]);                      // fits
            Should.Throw<Microsoft.Data.Sqlite.SqliteException>(
                () => InsertVector(connections, chunkId: 2, new float[1024]));           // wrong dimension rejected
        }
        finally
        {
            // Scope the pool clear to THIS database (see TempDatabase) — a global
            // ClearAllPools() races with parallel test classes' in-use connections.
            using (var conn = connections.Open())
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(conn);
            }
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void SwitchEmbeddingModel_rebuilds_vector_table_and_requeues()
    {
        using var db = new TempDatabase();
        SeedEmbeddedMessage(db.Connections);

        var migrator = new SchemaMigrator(db.Connections, NullLogger<SchemaMigrator>.Instance);
        var result = migrator.SwitchEmbeddingModel("qwen3-embedding:4b", 2560);

        result.OldModel.ShouldBe("mxbai-embed-large");
        result.OldDimensions.ShouldBe("1024");
        result.ChunksDeleted.ShouldBe(1);
        result.MessagesReset.ShouldBe(1);

        ReadMetadata(db.Connections, "embedding_model").ShouldBe("qwen3-embedding:4b");
        ReadMetadata(db.Connections, "embedding_dimensions").ShouldBe("2560");
        Count(db.Connections, "chunks").ShouldBe(0);
        Count(db.Connections, "chunk_embeddings").ShouldBe(0);
        Count(db.Connections, "messages", "embedded_at IS NULL").ShouldBe(1);
        VectorTableSql(db.Connections).ShouldContain("FLOAT[2560]");
        InsertVector(db.Connections, chunkId: 1, new float[2560]);
        Should.Throw<Microsoft.Data.Sqlite.SqliteException>(
            () => InsertVector(db.Connections, chunkId: 2, new float[1024]));
    }

    [Fact]
    public void SwitchEmbeddingModel_round_trips_back_to_original()
    {
        using var db = new TempDatabase();
        var migrator = new SchemaMigrator(db.Connections, NullLogger<SchemaMigrator>.Instance);

        migrator.SwitchEmbeddingModel("qwen3-embedding:4b", 2560);
        migrator.SwitchEmbeddingModel("mxbai-embed-large", 1024);

        ReadMetadata(db.Connections, "embedding_model").ShouldBe("mxbai-embed-large");
        ReadMetadata(db.Connections, "embedding_dimensions").ShouldBe("1024");
        VectorTableSql(db.Connections).ShouldContain("FLOAT[1024]");
        InsertVector(db.Connections, chunkId: 1, new float[1024]);
    }

    /// <summary>One message with one chunk + one 1024-dim vector, embedded_at stamped.</summary>
    private static void SeedEmbeddedMessage(ConnectionFactory connections)
    {
        using var conn = connections.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO messages (message_id, maildir_path, maildir_filename, folder, subject, indexed_at, embedded_at)
                VALUES ('seed@x', 'INBOX/cur', 'f1', 'INBOX', 'seeded', '2025-01-01T00:00:00.0000000+00:00', '2025-01-01T00:00:00.0000000+00:00')
                """;
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO chunks (message_id, chunk_index, chunk_text) VALUES (1, 0, 'hello')";
            cmd.ExecuteNonQuery();
        }
        InsertVector(connections, chunkId: 1, new float[1024]);
    }

    private static void InsertVector(ConnectionFactory connections, long chunkId, float[] vec)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO chunk_embeddings (chunk_id, embedding) VALUES ($id, $vec)";
        cmd.Parameters.AddWithValue("$id", chunkId);
        cmd.Parameters.Add("$vec", Microsoft.Data.Sqlite.SqliteType.Blob).Value = VectorBlob.Serialize(vec);
        cmd.ExecuteNonQuery();
    }

    private static string VectorTableSql(ConnectionFactory connections)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'chunk_embeddings'";
        return (string?)cmd.ExecuteScalar() ?? string.Empty;
    }

    private static string? ReadMetadata(ConnectionFactory connections, string key)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM metadata WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static long Count(ConnectionFactory connections, string table, string? where = null)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}" + (where is null ? "" : $" WHERE {where}");
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the real embedded 001_initial.sql resource out of Mailvec.Core.</summary>
    private static string LoadInitialSchemaSql()
    {
        var asm = typeof(SchemaMigrator).Assembly;
        var resource = asm.GetManifestResourceNames().First(n => n.EndsWith("001_initial.sql", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
        var connections = new ConnectionFactory(Microsoft.Extensions.Options.Options.Create(new ArchiveOptions { DatabasePath = dbPath }));
        try
        {
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

            // Post-migration: column exists, schema walked v2 forward to latest, prior row preserved.
            TableHasColumn(connections, "messages", "content_hash").ShouldBeTrue();
            ReadSchemaVersion(connections).ShouldBe(SchemaMigrator.LatestSchemaVersion);
            TableHasColumn(connections, "attachments", "extracted_text").ShouldBeTrue();
            TableHasColumn(connections, "chunks", "source").ShouldBeTrue();
            TableHasColumn(connections, "messages", "attachment_text").ShouldBeTrue();
            FtsHasColumn(connections, "attachment_text").ShouldBeTrue();

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
            // Scope the pool clear to THIS database (see TempDatabase) — a global
            // ClearAllPools() races with parallel test classes' in-use connections.
            using (var conn = connections.Open())
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(conn);
            }
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void Migrator_is_idempotent_when_already_at_latest()
    {
        using var db = new TempDatabase();
        // Second call should be a no-op and not throw.
        new SchemaMigrator(db.Connections, NullLogger<SchemaMigrator>.Instance).EnsureUpToDate();
        ReadSchemaVersion(db).ShouldBe(SchemaMigrator.LatestSchemaVersion);
    }

    [Fact]
    public void Migrator_walks_v1_database_forward_to_latest()
    {
        // Pre-eefd6fb DBs sit at v1 — no attachment_names column, no
        // attachments table, no content_hash, and a 4-column messages_fts
        // virtual table. The walk must apply 002 (FTS column add via
        // drop+rebuild, attachments table) and 003 (content_hash column)
        // in sequence, and the rebuild must repopulate the FTS index so
        // pre-existing rows remain searchable.
        var dir = Path.Combine(Path.GetTempPath(), "mailvec-migration-v1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "archive.sqlite");
        var connections = new ConnectionFactory(Microsoft.Extensions.Options.Options.Create(new ArchiveOptions { DatabasePath = dbPath }));
        try
        {
            using (var seed = connections.Open())
            {
                ApplyV1Schema(seed);
                InsertSampleMessage(seed, messageId: "v1legacy@x", subject: "needlesubject");
            }

            // Sanity: pre-migration shape.
            ReadSchemaVersion(connections).ShouldBe(1);
            TableHasColumn(connections, "messages", "attachment_names").ShouldBeFalse();
            TableHasColumn(connections, "messages", "content_hash").ShouldBeFalse();
            TableExists(connections, "attachments").ShouldBeFalse();

            new SchemaMigrator(connections, NullLogger<SchemaMigrator>.Instance).EnsureUpToDate();

            // Post-migration: schema bumped through 002 -> 003 -> 004 -> 005,
            // all expected columns present, attachments table created, FTS
            // table has both new columns, v4 attachment-text columns exist,
            // v5 messages.attachment_text wired through to FTS.
            ReadSchemaVersion(connections).ShouldBe(SchemaMigrator.LatestSchemaVersion);
            TableHasColumn(connections, "messages", "attachment_names").ShouldBeTrue();
            TableHasColumn(connections, "messages", "content_hash").ShouldBeTrue();
            TableExists(connections, "attachments").ShouldBeTrue();
            FtsHasColumn(connections, "attachment_names").ShouldBeTrue();
            FtsHasColumn(connections, "attachment_text").ShouldBeTrue();
            TableHasColumn(connections, "attachments", "extracted_text").ShouldBeTrue();
            TableHasColumn(connections, "messages", "attachment_text").ShouldBeTrue();
            TableHasColumn(connections, "chunks", "source").ShouldBeTrue();
            TableHasColumn(connections, "chunks", "attachment_id").ShouldBeTrue();

            // The pre-existing row survives and is still searchable via FTS,
            // i.e. the rebuild repopulated the index from messages.
            using var verify = connections.Open();
            using (var cmd = verify.CreateCommand())
            {
                cmd.CommandText = "SELECT subject FROM messages WHERE message_id = 'v1legacy@x'";
                var subj = cmd.ExecuteScalar() as string;
                subj.ShouldBe("needlesubject");
            }
            using (var cmd = verify.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'needlesubject'";
                Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
            }
        }
        finally
        {
            // Scope the pool clear to THIS database (see TempDatabase) — a global
            // ClearAllPools() races with parallel test classes' in-use connections.
            using (var conn = connections.Open())
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(conn);
            }
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// Apply only the v1 portion of the original schema — what a DB created
    /// before the eefd6fb "Index attachments" commit would look like. Includes
    /// the v1 messages_fts (4 columns, no attachment_names) and triggers, so
    /// the v2 migration's drop+rebuild path is exercised against the same
    /// shape it would meet in the wild.
    /// </summary>
    private static void ApplyV1Schema(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        var statements = new[]
        {
            "PRAGMA journal_mode = WAL",
            "PRAGMA foreign_keys = ON",
            // v1 messages — no attachment_names, no content_hash
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
                body_text TEXT,
                body_html TEXT,
                raw_headers TEXT,
                indexed_at TEXT NOT NULL,
                embedded_at TEXT,
                deleted_at TEXT
            )
            """,
            // v1 messages_fts — 4 columns
            """
            CREATE VIRTUAL TABLE messages_fts USING fts5(
                subject, from_name, from_address, body_text,
                content='messages', content_rowid='id',
                tokenize='porter unicode61'
            )
            """,
            """
            CREATE TRIGGER messages_ai AFTER INSERT ON messages BEGIN
                INSERT INTO messages_fts(rowid, subject, from_name, from_address, body_text)
                VALUES (new.id, new.subject, new.from_name, new.from_address, new.body_text);
            END
            """,
            """
            CREATE TRIGGER messages_ad AFTER DELETE ON messages BEGIN
                INSERT INTO messages_fts(messages_fts, rowid, subject, from_name, from_address, body_text)
                VALUES ('delete', old.id, old.subject, old.from_name, old.from_address, old.body_text);
            END
            """,
            """
            CREATE TRIGGER messages_au AFTER UPDATE ON messages BEGIN
                INSERT INTO messages_fts(messages_fts, rowid, subject, from_name, from_address, body_text)
                VALUES ('delete', old.id, old.subject, old.from_name, old.from_address, old.body_text);
                INSERT INTO messages_fts(rowid, subject, from_name, from_address, body_text)
                VALUES (new.id, new.subject, new.from_name, new.from_address, new.body_text);
            END
            """,
            // v1 had a chunks table (sans source/attachment_id, added in 004).
            """
            CREATE TABLE chunks (
                id           INTEGER PRIMARY KEY,
                message_id   INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                chunk_index  INTEGER NOT NULL,
                chunk_text   TEXT NOT NULL,
                token_count  INTEGER,
                UNIQUE(message_id, chunk_index)
            )
            """,
            "CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL)",
            "INSERT INTO metadata(key, value) VALUES ('schema_version', '1')",
        };
        foreach (var sql in statements)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    private static bool TableExists(ConnectionFactory connections, string table)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','view') AND name = $name";
        cmd.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static bool FtsHasColumn(ConnectionFactory connections, string column)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'messages_fts'";
        var sql = (string?)cmd.ExecuteScalar() ?? string.Empty;
        return sql.Contains(column, StringComparison.Ordinal);
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
            // v2 introduced the attachments table — needs to exist on the
            // seed for downstream migrations (004) to ALTER it.
            """
            CREATE TABLE attachments (
                id            INTEGER PRIMARY KEY,
                message_id    INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                part_index    INTEGER NOT NULL,
                filename      TEXT,
                content_type  TEXT,
                size_bytes    INTEGER,
                UNIQUE(message_id, part_index)
            )
            """,
            // v2 also has the chunks table so 004's ALTER TABLE chunks runs.
            """
            CREATE TABLE chunks (
                id           INTEGER PRIMARY KEY,
                message_id   INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                chunk_index  INTEGER NOT NULL,
                chunk_text   TEXT NOT NULL,
                token_count  INTEGER,
                UNIQUE(message_id, chunk_index)
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
