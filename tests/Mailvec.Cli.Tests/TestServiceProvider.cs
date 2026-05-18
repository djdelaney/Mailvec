using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailvec.Cli.Tests;

/// <summary>
/// Spins up a temp SQLite DB with the schema applied, plus a minimal
/// <see cref="IServiceProvider"/> wired with the dependencies the CLI
/// commands' <c>Execute</c> seams need. Mirrors a slimmed-down
/// <c>CliServices.Build()</c> — no Console wiring, no Ollama HttpClient
/// (none of the destructive commands tested here hit Ollama).
/// </summary>
public sealed class TestServiceProvider : IDisposable
{
    public string DirectoryPath { get; }
    public string DatabasePath { get; }
    public ServiceProvider Services { get; }
    public ConnectionFactory Connections { get; }

    public TestServiceProvider()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "mailvec-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        DatabasePath = Path.Combine(DirectoryPath, "archive.sqlite");

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.Configure<ArchiveOptions>(o => o.DatabasePath = DatabasePath);
        services.AddSingleton<ConnectionFactory>();
        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<MessageRepository>();
        services.AddSingleton<MetadataRepository>();
        services.AddSingleton<ChunkRepository>();

        Services = services.BuildServiceProvider();
        Connections = Services.GetRequiredService<ConnectionFactory>();
        Services.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
    }

    public void Dispose()
    {
        Services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DirectoryPath, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
