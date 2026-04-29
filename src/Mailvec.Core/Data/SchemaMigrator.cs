using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Mailvec.Core.Data;

public sealed class SchemaMigrator(ConnectionFactory connections, ILogger<SchemaMigrator> logger)
{
    private const int LatestSchemaVersion = 2;

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
            logger.LogInformation("Applying initial schema (version {Version})", LatestSchemaVersion);
            ExecuteScript(conn, LoadEmbeddedSql("001_initial.sql"));
            return;
        }

        // Future migrations: apply scripts for versions current+1 .. LatestSchemaVersion.
        throw new InvalidOperationException(
            $"Schema is at version {current}; latest is {LatestSchemaVersion}. " +
            $"Migrations are not yet implemented for this gap.");
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
