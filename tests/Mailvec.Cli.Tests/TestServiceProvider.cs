using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mailvec.Cli.Tests;

/// <summary>
/// Spins up a temp SQLite DB with the schema applied, plus a minimal
/// <see cref="IServiceProvider"/> wired with the dependencies the CLI
/// commands' <c>Execute</c> seams need. Mirrors a slimmed-down
/// <c>CliServices.Build()</c> — no Console wiring, no Ollama HttpClient
/// (none of the commands tested here hit Ollama).
/// </summary>
public sealed class TestServiceProvider : IDisposable
{
    public string DirectoryPath { get; }
    public string DatabasePath { get; }
    public ServiceProvider Services { get; private set; }
    public ConnectionFactory Connections { get; private set; }

    private readonly ServiceCollection _services;

    public TestServiceProvider()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "mailvec-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        DatabasePath = Path.Combine(DirectoryPath, "archive.sqlite");

        _services = new ServiceCollection();
        _services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        _services.Configure<ArchiveOptions>(o => o.DatabasePath = DatabasePath);
        _services.AddSingleton<ConnectionFactory>();
        _services.AddSingleton<SchemaMigrator>();
        _services.AddSingleton<MessageRepository>();
        _services.AddSingleton<MetadataRepository>();
        _services.AddSingleton<ChunkRepository>();
        _services.AddSingleton<KeywordSearchService>();

        Services = _services.BuildServiceProvider();
        Connections = Services.GetRequiredService<ConnectionFactory>();
        Services.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
    }

    /// <summary>
    /// Register an extra Options binding before the service provider is
    /// built. Tests call this then <see cref="Rebuild"/> when they need
    /// command-specific options like IngestOptions / OllamaOptions /
    /// FastmailOptions that the default provider doesn't include.
    /// </summary>
    public TestServiceProvider AddOption<T>(Action<T> configure) where T : class
    {
        _services.Configure(configure);
        return this;
    }

    /// <summary>Rebuilds the provider after additional Configure calls.</summary>
    public ServiceProvider Rebuild()
    {
        Services.Dispose();
        Services = _services.BuildServiceProvider();
        Connections = Services.GetRequiredService<ConnectionFactory>();
        return Services;
    }

    public void Dispose()
    {
        Services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DirectoryPath, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
