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
    IOptions<OllamaOptions>? ollamaOptions = null,
    // The resolved embedding profile, when this process registered one via
    // AddMailvecEmbedding (DI fills it; test constructions may omit it and
    // fall back to options-derived identity with empty suffix transforms).
    // Needed because suffix transforms live only on profiles: an
    // options-derived config hash would silently ignore them.
    Mailvec.Core.Embedding.ResolvedEmbeddingProfile? embeddingProfile = null)
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
    // v7 adds messages.embed_epoch, the monotonic re-queue counter that lets
    // the embedder's guarded chunk write detect re-queues that don't change
    // content_hash (attachment re-extraction, OCR write-back, backfills).
    // v8 adds sync_state.folder (+ index): folder membership for search, so a
    // message living in several folders (Gmail All Mail + labels) is findable
    // under each. No backfill — the scanner populates it on its next full scan.
    // v9 adds sync_state.file_mtime_utc / file_size, the observed file identity
    // the scanner's fast path compares for equality — replacing an inequality
    // against scan time that let a content change with a preserved/backdated
    // mtime be skipped on every future scan. No backfill; NULL means "no
    // recorded identity" and the scanner records it on the next pass.
    // v11 adds metadata.embedding_space_id (stamped in SQL from the DB's own
    // stored model/dimensions) and metadata.embedding_config_hash (stamped in
    // code by StampConfigHashIfMissing — it covers config-side text
    // transforms SQL cannot see). Identity only; vectors are untouched.
    public const int LatestSchemaVersion = 11;

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

        if (current > LatestSchemaVersion)
        {
            // Downgrade guard. An older binary running against a newer DB is
            // not "already up to date" — it silently lacks whatever invariant
            // the newer schema exists to enforce (e.g. a pre-v7 binary never
            // bumps embed_epoch, quietly reintroducing the mid-embed re-queue
            // clobber that column prevents). Refusing loudly beats corrupting
            // quietly: the fix is redeploying current binaries
            // (ops/redeploy.sh) or restoring a matching DB snapshot.
            throw new InvalidOperationException(
                $"Database schema is v{current} but this binary only knows v{LatestSchemaVersion} — " +
                "it is older than the database and may silently violate newer data invariants. " +
                "Update the binaries (ops/redeploy.sh, or rebuild the MCPB bundle) or restore a " +
                "database snapshot that matches this binary (ops/import-db.sh).");
        }

        if (current == LatestSchemaVersion)
        {
            logger.LogDebug("Schema already at version {Version}", current);
            StampConfigHashIfMissing(conn);
            return;
        }

        if (current == 0)
        {
            // Identity comes from the resolved profile when one is registered
            // — a fresh database created under a hosted profile must stamp
            // the ASSERTED space id, not an Ollama derivation of a hosted
            // wire model. The Ollama-options fallback covers legacy test
            // constructions; every executable that can create the schema
            // (indexer included, via the credential-free identity
            // registration) supplies the profile in production.
            var (model, dims, spaceId) = FreshIdentity();
            logger.LogInformation(
                "Applying initial schema (stamping version {Version}, embedding model {Model} @{Dim}d, space {SpaceId})",
                LatestSchemaVersion, model, dims, spaceId);
            var initialSql = LoadEmbeddedSql("001_initial.sql");
            AssertBaselineStampsLatest(initialSql);
            ExecuteScript(conn, SubstituteEmbeddingConfig(initialSql, model, dims, spaceId),
                guardAtLeast: 1); // skip if another starter already initialized the schema
            StampConfigHashIfMissing(conn);
            return;
        }

        for (var v = current + 1; v <= LatestSchemaVersion; v++)
        {
            var (fileName, sql) = LoadMigrationForVersion(v);
            logger.LogInformation("Applying migration {File} ({From} -> {To})", fileName, v - 1, v);
            ExecuteScript(conn, sql, stampVersion: v, guardAtLeast: v);
        }

        StampConfigHashIfMissing(conn);
    }

    private (string Model, int Dimensions, string SpaceId) FreshIdentity()
    {
        if (embeddingProfile is not null)
            return (embeddingProfile.WireModel, embeddingProfile.OutputDimensions, embeddingProfile.SpaceId);
        var opts = ollamaOptions?.Value ?? new OllamaOptions();
        return (opts.EmbeddingModel, opts.EmbeddingDimensions,
            Embedding.EmbeddingSpace.LegacySpaceId(opts.EmbeddingModel, opts.EmbeddingDimensions));
    }

    /// <summary>
    /// Stamps <c>metadata.embedding_config_hash</c> when absent — and ONLY
    /// when this binary's configured model + dimensions agree with what the
    /// database already stores. The hash is a provenance claim ("this is how
    /// the stored vectors were produced"), and config that disagrees with the
    /// stored identity is exactly the situation in which that claim would be
    /// false — those databases stay unstamped until the mismatch is resolved
    /// (the embedder refuses to run against them anyway). Never overwrites:
    /// an existing hash is an observation this method has no authority over.
    /// Runs on every EnsureUpToDate, so a database migrated by a binary with
    /// mismatched config self-heals the first time a correctly-configured
    /// binary opens it.
    /// </summary>
    private void StampConfigHashIfMissing(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        string? Read(string key)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM metadata WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }

        if (Read(Embedding.EmbeddingSpace.ConfigHashKey) is not null) return;

        var opts = ollamaOptions?.Value ?? new OllamaOptions();
        var (configModel, configDims) = embeddingProfile is not null
            ? (embeddingProfile.WireModel, embeddingProfile.OutputDimensions)
            : (opts.EmbeddingModel, opts.EmbeddingDimensions);
        var storedModel = Read("embedding_model");
        var storedDims = Read("embedding_dimensions");
        var storedSpaceId = Read(Embedding.EmbeddingSpace.SpaceIdKey);
        if (storedModel is null || storedDims is null || storedSpaceId is null) return;
        if (!string.Equals(storedModel, configModel, StringComparison.Ordinal)) return;
        if (storedDims != configDims.ToString(CultureInfo.InvariantCulture)) return;

        var (derivedSpaceId, hash) = embeddingProfile is not null
            ? Embedding.EmbeddingSpace.ForProfile(embeddingProfile)
            : Embedding.EmbeddingSpace.FromOllamaOptions(opts);
        // A space id this config can't reproduce (a future hosted profile's
        // asserted id, or a hand-edited value) is not ours to describe.
        if (!string.Equals(storedSpaceId, derivedSpaceId, StringComparison.Ordinal)) return;

        using var stamp = conn.CreateCommand();
        stamp.CommandText = """
            INSERT INTO metadata(key, value) VALUES($k, $v)
            ON CONFLICT(key) DO NOTHING
            """;
        stamp.Parameters.AddWithValue("$k", Embedding.EmbeddingSpace.ConfigHashKey);
        stamp.Parameters.AddWithValue("$v", hash);
        stamp.ExecuteNonQuery();
        logger.LogInformation("Stamped embedding_config_hash for space {SpaceId}", storedSpaceId);
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

    // The schema_version stamp must commit in the SAME transaction as the
    // migration statements. A crash after the script committed but before a
    // separate bump would leave the version unbumped, so the (non-idempotent,
    // ALTER-based) migration re-runs on the next start and throws "duplicate
    // column name" — permanently wedging every service until metadata is
    // hand-edited. Internal (not private) so tests can exercise the
    // rollback semantics directly.
    internal static void ExecuteScript(
        Microsoft.Data.Sqlite.SqliteConnection conn, string script, int? stampVersion = null, int? guardAtLeast = null)
    {
        // PRAGMA journal_mode = WAL must run outside a transaction, so we run
        // PRAGMA-only statements first and the rest inside a transaction.
        // (PRAGMAs here are idempotent, so a concurrent starter running them too
        // is harmless.)
        var statements = SqlScriptSplitter.Split(script);

        foreach (var stmt in statements.Where(s => s.TrimStart().StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = stmt;
            cmd.ExecuteNonQuery();
        }

        // BEGIN IMMEDIATE takes the write lock now, so two services cold-starting
        // against the same DB serialize here rather than both walking into the
        // (non-idempotent, CREATE/ALTER-based) DDL below and one crashing on
        // "table exists"/"duplicate column".
        using var tx = conn.BeginTransaction(deferred: false);

        // Whoever loses the race for the lock re-reads the version inside the
        // transaction: if the winner already applied this, skip — the empty
        // transaction rolls back on dispose. This closes the concurrent
        // double-apply crash-loop window (the read in EnsureUpToDate is outside
        // any lock, so both callers can see "needs migration").
        if (guardAtLeast is int guard && ReadSchemaVersionInTx(conn, tx) >= guard)
            return;

        foreach (var stmt in statements.Where(s => !s.TrimStart().StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = stmt;
            cmd.ExecuteNonQuery();
        }
        if (stampVersion is int version)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO metadata(key, value) VALUES('schema_version', $v)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            cmd.Parameters.AddWithValue("$v", version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
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

    // Same read, but enlisted in the caller's write transaction so the value is
    // consistent with the lock ExecuteScript already holds.
    private static int ReadSchemaVersionInTx(
        Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
        try
        {
            var raw = cmd.ExecuteScalar() as string;
            return raw is null ? 0 : int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            return 0; // no metadata table yet (fresh DB)
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
    internal static string SubstituteEmbeddingConfig(string sql, string model, int dimensions, string? spaceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 8192);
        if (model.Contains('\''))
            throw new ArgumentException($"Embedding model name must not contain a single quote: {model}", nameof(model));
        spaceId ??= Embedding.EmbeddingSpace.LegacySpaceId(model, dimensions);
        if (spaceId.Contains('\''))
            throw new ArgumentException($"Space id must not contain a single quote: {spaceId}", nameof(spaceId));

        var dims = dimensions.ToString(CultureInfo.InvariantCulture);
        // Order matters: FLOAT[1024] must be rewritten before the
        // ('embedding_dimensions', '1024') seed so the two '1024' tokens
        // can't be confused; the model token is matched in its quoted form
        // because the schema comments mention mxbai-embed-large unquoted (and
        // the space-id seed embeds the name without its own quotes, so the
        // quoted token still appears exactly once). The space-id seed is
        // rewritten first, while its default literal is still intact.
        sql = ReplaceExactlyOnce(sql, "'ollama:mxbai-embed-large:1024'", $"'{spaceId}'");
        sql = ReplaceExactlyOnce(sql, "FLOAT[1024]", $"FLOAT[{dims}]");
        sql = ReplaceExactlyOnce(sql, "'mxbai-embed-large'", $"'{model}'");
        sql = ReplaceExactlyOnce(sql, "('embedding_dimensions', '1024')", $"('embedding_dimensions', '{dims}')");
        return sql;
    }

    /// <summary>
    /// 001_initial.sql carries its own <c>schema_version</c> literal, and the
    /// fresh-database path applies it verbatim — the log line above says
    /// "stamping version {LatestSchemaVersion}" but the value that actually
    /// lands comes from the SQL. If the two drift, a fresh database is created
    /// WITH the newest columns but stamped at an older version, and the very
    /// next startup replays the migrations that add them.
    ///
    /// <para>That surfaces as <c>SQLite Error 1: 'duplicate column name: …'</c>
    /// from inside a migration — an error that names the column and says
    /// nothing about the stamp that caused it, on the one code path (fresh
    /// install) least likely to be exercised before shipping. Adding a
    /// migration means bumping BOTH <see cref="LatestSchemaVersion"/> and the
    /// literal in 001; this makes forgetting either one a named failure at the
    /// point of the mistake.</para>
    /// </summary>
    private static void AssertBaselineStampsLatest(string initialSql)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            initialSql, @"\('schema_version',\s*'(\d+)'\)");
        if (!m.Success)
            throw new InvalidOperationException(
                "001_initial.sql has no ('schema_version', 'N') row — SchemaMigrator cannot verify "
                + "that a fresh database is stamped at the version its columns actually represent.");
        var stamped = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (stamped != LatestSchemaVersion)
            throw new InvalidOperationException(
                $"001_initial.sql stamps schema_version {stamped} but SchemaMigrator.LatestSchemaVersion is "
                + $"{LatestSchemaVersion}. A fresh database would be created with the v{LatestSchemaVersion} "
                + $"columns and then replay migrations {stamped + 1}..{LatestSchemaVersion} over them. "
                + "Bump the literal in 001_initial.sql to match.");
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

    /// <summary>
    /// The complete embedding-space identity a switch to (model, dimensions)
    /// would stamp — the SAME derivation SwitchEmbeddingModel writes, exposed
    /// so the no-op decision can compare against it. When the target matches
    /// the active profile's model+dims, the identity is the PROFILE's (for
    /// hosted profiles the asserted SpaceId — an Ollama derivation of a
    /// hosted wire model would be refused by the very embedder this feeds);
    /// any divergence is an Ollama-side experiment and gets the legacy
    /// derivation. Model/dims alone are NOT a no-op test: the same nominal
    /// model at the same width on a different provider, or with changed
    /// text transforms, is a different space.
    /// </summary>
    public (string SpaceId, string ConfigHash) TargetIdentity(string model, int dimensions)
    {
        var matchesProfile = embeddingProfile is not null
            && string.Equals(embeddingProfile.WireModel, model, StringComparison.Ordinal)
            && embeddingProfile.OutputDimensions == dimensions;
        if (matchesProfile)
            return Embedding.EmbeddingSpace.ForProfile(embeddingProfile!);

        var spaceId = Embedding.EmbeddingSpace.LegacySpaceId(model, dimensions);
        var prefix = embeddingProfile?.QueryPrefix ?? ollamaOptions?.Value.QueryInstructionPrefix ?? "";
        return (spaceId, Embedding.EmbeddingSpace.ComputeConfigHash(
            spaceId, model, dimensions, prefix,
            embeddingProfile?.QuerySuffix ?? "",
            embeddingProfile?.DocumentPrefix ?? "",
            embeddingProfile?.DocumentSuffix ?? ""));
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

        // The digest and sentinel fingerprints are observations about the OLD
        // serving function; the embedder re-observes and stamps new ones on
        // its first run. Cleared in this same transaction so a crash can't
        // leave the new identity carrying stale observations (which would
        // refuse the very rebuild switch-model exists to enable).
        Exec($"DELETE FROM metadata WHERE key = '{Embedding.EmbeddingSpace.ModelDigestKey}'");
        Exec($"DELETE FROM metadata WHERE key LIKE '{Embedding.EmbeddingSpace.SentinelKeyPrefix}%'");

        Exec("DROP TABLE chunk_embeddings");
        // vec0 DDL can't take parameters; dimensions is range-validated above.
        Exec($"CREATE VIRTUAL TABLE chunk_embeddings USING vec0(chunk_id INTEGER PRIMARY KEY, embedding FLOAT[{dimensions.ToString(CultureInfo.InvariantCulture)}])");
        var chunksDeleted = Exec("DELETE FROM chunks");
        var messagesReset = Exec("UPDATE messages SET embedded_at = NULL, embed_epoch = embed_epoch + 1");

        // Space id + config hash are stamped in the SAME transaction as the
        // model/dimension rewrite — a crash between them would leave the new
        // model carrying the old space's identity, which is precisely the
        // false-compatibility claim the v11 columns exist to prevent. The
        // hash is computed from the CLI's resolved config (the
        // embedding-experiments flow sets Ollama__* env overrides for the
        // switch invocation too, so the transforms here match the ones the
        // embedder will run with); the text transforms come from the resolved
        // profile when one is registered — suffixes exist only there.
        var (spaceId, configHash) = TargetIdentity(model, dimensions);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO metadata(key, value) VALUES
                    ('embedding_model', $m), ('embedding_dimensions', $d),
                    ('embedding_space_id', $s), ('embedding_config_hash', $h)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            cmd.Parameters.AddWithValue("$m", model);
            cmd.Parameters.AddWithValue("$d", dimensions.ToString(CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$s", spaceId);
            cmd.Parameters.AddWithValue("$h", configHash);
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
