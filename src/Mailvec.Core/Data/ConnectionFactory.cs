using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mailvec.Core.Options;

namespace Mailvec.Core.Data;

public sealed class ConnectionFactory
{
    private readonly string _connectionString;
    private readonly string? _vecExtensionPath;

    public ConnectionFactory(IOptions<ArchiveOptions> options)
    {
        var dbPath = PathExpansion.Expand(options.Value.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            DefaultTimeout = 30,
        }.ToString();

        _vecExtensionPath = ResolveVecExtension(options.Value.SqliteVecExtensionPath);
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        if (_vecExtensionPath is not null)
        {
            conn.EnableExtensions(true);
            conn.LoadExtension(_vecExtensionPath);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        cmd.ExecuteNonQuery();

        return conn;
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
