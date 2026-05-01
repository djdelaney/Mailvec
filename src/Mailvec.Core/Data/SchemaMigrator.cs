using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Mailvec.Core.Data;

public sealed class SchemaMigrator(ConnectionFactory connections, ILogger<SchemaMigrator> logger)
{
    // Bump this when adding a new migration file under schema/migrations/.
    // Fresh DBs get 001_initial.sql which stamps schema_version directly;
    // existing DBs at an older version walk migrations forward one at a time.
    // Keep 001_initial.sql's seed value of schema_version in lockstep with
    // this constant for fresh installs.
    private const int LatestSchemaVersion = 3;

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
            logger.LogInformation("Applying initial schema (stamping version {Version})", LatestSchemaVersion);
            ExecuteScript(conn, LoadEmbeddedSql("001_initial.sql"));
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
