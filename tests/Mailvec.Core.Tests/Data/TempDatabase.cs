using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Tests.Data;

/// <summary>
/// Spins up an isolated SQLite DB in a temp directory with the schema applied.
/// Disposed by xUnit when the test class ends.
/// </summary>
public sealed class TempDatabase : IDisposable
{
    public string DirectoryPath { get; }
    public string DatabasePath { get; }
    public ConnectionFactory Connections { get; }

    public TempDatabase()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "mailvec-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        DatabasePath = Path.Combine(DirectoryPath, "archive.sqlite");

        var options = Microsoft.Extensions.Options.Options.Create(new ArchiveOptions { DatabasePath = DatabasePath });
        Connections = new ConnectionFactory(options);

        var migrator = new SchemaMigrator(Connections, NullLogger<SchemaMigrator>.Instance);
        migrator.EnsureUpToDate();
    }

    public void Dispose()
    {
        // SQLite holds the file open until the connection pool is cleared.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DirectoryPath, recursive: true); }
        catch (IOException) { /* best effort cleanup */ }
    }
}
