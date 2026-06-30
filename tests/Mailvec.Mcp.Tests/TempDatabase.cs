using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mailvec.Mcp.Tests;

/// <summary>
/// Same shape as the helper in Mailvec.Core.Tests — duplicated here so
/// Mailvec.Mcp.Tests doesn't need a project reference to the Core tests
/// project. ~30 LOC; cheaper than carving out a shared TestSupport project.
/// </summary>
public sealed class TempDatabase : IDisposable
{
    public string DirectoryPath { get; }
    public string DatabasePath { get; }
    public ConnectionFactory Connections { get; }

    public TempDatabase()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "mailvec-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        DatabasePath = Path.Combine(DirectoryPath, "archive.sqlite");

        var options = Microsoft.Extensions.Options.Options.Create(new ArchiveOptions { DatabasePath = DatabasePath });
        Connections = new ConnectionFactory(options);

        var migrator = new SchemaMigrator(Connections, NullLogger<SchemaMigrator>.Instance);
        migrator.EnsureUpToDate();
    }

    public void Dispose()
    {
        // Scope the clear to THIS database's pool (unique per DataSource) — a
        // global SqliteConnection.ClearAllPools() races with other test classes
        // running in parallel (xUnit parallelizes classes by default), disposing
        // their in-use native connection handles mid-test → ObjectDisposedException
        // or silently-wrong query results.
        using (var conn = Connections.Open())
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(conn);
        }
        try { Directory.Delete(DirectoryPath, recursive: true); }
        catch (IOException) { /* best effort cleanup */ }
    }
}
