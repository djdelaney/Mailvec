using System.Globalization;
using System.Reflection;
using Mailvec.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Data;

// ollamaOptions is optional so the many direct test constructions keep
// working with the mxbai-embed-large/1024 defaults baked into OllamaOptions.
// DI always supplies it, so production fresh DBs are created with whatever
// model/dimensions the binary is configured for.
public sealed class SchemaMigrator(
    ConnectionFactory connections,
    ILogger<SchemaMigrator> logger,
    IOptions<OllamaOptions>? ollamaOptions = null)
{
    // Bump this when adding a new migration file under schema/migrations/.
    // Fresh DBs get 001_initial.sql which stamps schema_version directly;
    // existing DBs at an older version walk migrations forward one at a time.
    // Keep 001_initial.sql's seed value of schema_version in lockstep with
    // this constant for fresh installs.
    // v4 adds attachment text extraction columns (attachments.extracted_text /
    // extracted_at / extraction_status) and chunk-source tracking
    // (chunks.source / chunks.attachment_id). There is no in-place migration
    // from v3 — re-extraction would mean re-walking every Maildir file anyway,
    // so the upgrade path is "drop the DB and let the indexer rebuild".
    // v5 wires extracted attachment text into messages_fts via a denormalized
    // messages.attachment_text column. The 005 migration backfills from
    // attachments.extracted_text in pure SQL, so v4 -> v5 is a fully in-place
    // upgrade (no .eml re-walk required).
    // v6 adds an index on messages.indexed_at so HealthService can resolve
    // MAX(indexed_at) in O(log n) instead of full-scanning the table — fixes
    // multi-second /health latency on real-sized archives.
    public const int LatestSchemaVersion = 6;

    /// <summary>
    /// Read the schema version stored in the metadata table, without applying
    /// migrations. Returns 0 for a fresh / nonexistent DB. Used by `mailvec
    /// doctor` to surface "DB is at v3, binary expects v5" without having to
    /// open a connection that triggers migration as a side effect.
    /// </summary>
    public int GetCurrentVersion()
    {
        using var conn = connections.Open();
        return ReadSchemaVersion(conn);
    }

    public void EnsureUpToDate()
    {
        using var conn = connections.Open();
        var current = ReadSchemaVersion(conn);

        if (current >= LatestSchemaVersion)
        {
            logger.LogDebug("Schema already at version {Version}", current);
            return;
        }

        if (current == 0)
        {
            var opts = ollamaOptions?.Value ?? new OllamaOptions();
            logger.LogInformation(
                "Applying initial schema (stamping version {Version}, embedding model {Model} @{Dim}d)",
                LatestSchemaVersion, opts.EmbeddingModel, opts.EmbeddingDimensions);
            ExecuteScript(conn, SubstituteEmbeddingConfig(
                LoadEmbeddedSql("001_initial.sql"), opts.EmbeddingModel, opts.EmbeddingDimensions));
            return;
        }

        for (var v = current + 1; v <= LatestSchemaVersion; v++)
        {
            var (fileName, sql) = LoadMigrationForVersion(v);
            logger.LogInformation("Applying migration {File} ({From} -> {To})", fileName, v - 1, v);
            ExecuteScript(conn, sql);
            BumpSchemaVersion(conn, v);
        }
    }

    /// <summary>
    /// Looks for an embedded resource whose basename matches "{NNN}_*.sql"
    /// where NNN is the zero-padded target version. Avoids requiring a
    /// hand-maintained version-to-filename table here.
    /// </summary>
    private static (string FileName, string Sql) LoadMigrationForVersion(int version)
    {
        var prefix = $"{version:D3}_";
        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames()
            .FirstOrDefault(name =>
            {
                if (!name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) return false;
                // Match the basename, not the full namespaced resource path,
                // so "Mailvec.Core.Schema.migrations.003_message_body_hash.sql"
                // matches prefix "003_".
                var lastDot = name.LastIndexOf('.', name.Length - 5); // skip ".sql"
                var basename = lastDot >= 0 ? name[(lastDot + 1)..] : name;
                return basename.StartsWith(prefix, StringComparison.Ordinal);
            })
            ?? throw new InvalidOperationException(
                $"No embedded migration resource found for schema version {version} (expected basename matching '{prefix}*.sql' under schema/migrations/).");

        using var stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return (FileName: resource, Sql: reader.ReadToEnd());
    }

    private static void ExecuteScript(Microsoft.Data.Sqlite.SqliteConnection conn, string script)
    {
        // PRAGMA journal_mode = WAL must run outside a transaction, so we run
        // PRAGMA-only statements first and the rest inside a transaction.
        var statements = SqlScriptSplitter.Split(script);

        foreach (var stmt in statements.Where(s => s.TrimStart().StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = stmt;
            cmd.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        foreach (var stmt in statements.Where(s => !s.TrimStart().StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = stmt;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void BumpSchemaVersion(Microsoft.Data.Sqlite.SqliteConnection conn, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO metadata(key, value) VALUES('schema_version', $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$v", version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private static int ReadSchemaVersion(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT value FROM metadata WHERE key = 'schema_version';
            """;
        try
        {
            var raw = cmd.ExecuteScalar() as string;
            return raw is null ? 0 : int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // "no such table: metadata" -> fresh DB, schema not yet applied.
            return 0;
        }
    }

    /// <summary>
    /// Rewrites 001_initial.sql's embedding literals (the vec0 column
    /// dimension and the metadata seed) to the configured model/dimensions.
    /// Runs on every fresh-DB creation, including the mxbai default (an
    /// identity rewrite), so the path is always exercised. Each target token
    /// must appear exactly once in the script — a schema edit that breaks
    /// that assumption fails loudly here instead of silently shipping a DB
    /// whose vec0 dimension disagrees with config.
    /// </summary>
    internal static string SubstituteEmbeddingConfig(string sql, string model, int dimensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 8192);
        if (model.Contains('\''))
            throw new ArgumentException($"Embedding model name must not contain a single quote: {model}", nameof(model));

        var dims = dimensions.ToString(CultureInfo.InvariantCulture);
        // Order matters: FLOAT[1024] must be rewritten before the
        // ('embedding_dimensions', '1024') seed so the two '1024' tokens
        // can't be confused; the model token is matched in its quoted form
        // because the schema comments mention mxbai-embed-large unquoted.
        sql = ReplaceExactlyOnce(sql, "FLOAT[1024]", $"FLOAT[{dims}]");
        sql = ReplaceExactlyOnce(sql, "'mxbai-embed-large'", $"'{model}'");
        sql = ReplaceExactlyOnce(sql, "('embedding_dimensions', '1024')", $"('embedding_dimensions', '{dims}')");
        return sql;
    }

    private static string ReplaceExactlyOnce(string sql, string token, string replacement)
    {
        var first = sql.IndexOf(token, StringComparison.Ordinal);
        if (first < 0)
            throw new InvalidOperationException(
                $"Schema substitution token '{token}' not found in 001_initial.sql — the schema and SchemaMigrator.SubstituteEmbeddingConfig have drifted.");
        if (sql.IndexOf(token, first + token.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException(
                $"Schema substitution token '{token}' appears more than once in 001_initial.sql — refusing an ambiguous rewrite.");
        return string.Concat(sql.AsSpan(0, first), replacement, sql.AsSpan(first + token.Length));
    }

    public sealed record SwitchModelResult(
        string? OldModel, string? OldDimensions, long ChunksDeleted, long MessagesReset);

    /// <summary>
    /// The sanctioned way to change a database's embedding model: drops and
    /// recreates chunk_embeddings with the new dimension, clears all chunks,
    /// re-queues every message for embedding, and updates the metadata the
    /// embedder's startup check validates against. One transaction — vec0
    /// DDL inside a transaction is the same pattern ExecuteScript already
    /// uses for fresh DBs. The embedder must be (re)started with matching
    /// Ollama:EmbeddingModel / EmbeddingDimensions config afterwards.
    /// </summary>
    public SwitchModelResult SwitchEmbeddingModel(string model, int dimensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 8192);
        if (model.Contains('\''))
            throw new ArgumentException($"Embedding model name must not contain a single quote: {model}", nameof(model));

        using var conn = connections.Open();
        using var tx = conn.BeginTransaction();

        string? Scalar(string sqlText)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sqlText;
            return cmd.ExecuteScalar()?.ToString();
        }

        long Exec(string sqlText)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sqlText;
            return cmd.ExecuteNonQuery();
        }

        var oldModel = Scalar("SELECT value FROM metadata WHERE key = 'embedding_model'");
        var oldDims = Scalar("SELECT value FROM metadata WHERE key = 'embedding_dimensions'");

        Exec("DROP TABLE chunk_embeddings");
        // vec0 DDL can't take parameters; dimensions is range-validated above.
        Exec($"CREATE VIRTUAL TABLE chunk_embeddings USING vec0(chunk_id INTEGER PRIMARY KEY, embedding FLOAT[{dimensions.ToString(CultureInfo.InvariantCulture)}])");
        var chunksDeleted = Exec("DELETE FROM chunks");
        var messagesReset = Exec("UPDATE messages SET embedded_at = NULL");

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO metadata(key, value) VALUES('embedding_model', $m), ('embedding_dimensions', $d)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            cmd.Parameters.AddWithValue("$m", model);
            cmd.Parameters.AddWithValue("$d", dimensions.ToString(CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        logger.LogInformation(
            "Switched embedding model {OldModel}@{OldDims} -> {Model}@{Dims}: {Chunks} chunks dropped, {Messages} messages re-queued",
            oldModel, oldDims, model, dimensions, chunksDeleted, messagesReset);
        return new SwitchModelResult(oldModel, oldDims, chunksDeleted, messagesReset);
    }

    private static string LoadEmbeddedSql(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded resource {fileName} not found in {asm.GetName().Name}");

        using var stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
