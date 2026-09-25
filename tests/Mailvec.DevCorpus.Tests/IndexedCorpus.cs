using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Indexer.Services;
using Mailvec.Parsing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mailvec.DevCorpus.Tests;

/// <summary>
/// A generated corpus, indexed once by the real scanner with the real
/// in-process parser and attachment extractor — the same path
/// `dotnet run --project src/Mailvec.Indexer` takes over it.
/// </summary>
public class IndexedCorpus : IDisposable
{
    public const int Filler = 20;

    public string Root { get; }
    public Corpus Corpus { get; }
    public MaildirScanner.ScanResult Scan { get; }
    public ConnectionFactory Connections { get; }

    public IndexedCorpus() : this(new CorpusOptions(Hazards: false, Filler: Filler)) { }

    protected IndexedCorpus(CorpusOptions options)
    {
        var temp = TempDir.Create("mailvec-devcorpus-");
        Root = CorpusWriter.Write(Path.Combine(temp, "corpus"), options, sharedConfigPath: TempDir.NoSharedConfig);
        Corpus = CorpusWriter.Build(options);

        Connections = new ConnectionFactory(Microsoft.Extensions.Options.Options.Create(new ArchiveOptions
        {
            DatabasePath = Path.Combine(Root, "state", "archive.sqlite"),
        }));
        new SchemaMigrator(Connections, NullLogger<SchemaMigrator>.Instance).EnsureUpToDate();

        var scanner = new MaildirScanner(
            Microsoft.Extensions.Options.Options.Create(new IngestOptions { MaildirRoot = Path.Combine(Root, "Mail") }),
            new InProcessParser(new AttachmentTextExtractor(new IndexerOptions().AttachmentMaxBytes, NullLogger<AttachmentTextExtractor>.Instance)),
            new MessageRepository(Connections),
            new ChunkRepository(Connections),
            new SyncStateRepository(Connections),
            Connections,
            NullLogger<MaildirScanner>.Instance);
        Scan = scanner.ScanAll();
    }

    public T Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] args)
    {
        using var conn = Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        return read(reader);
    }

    public void Dispose()
    {
        using (var conn = Connections.Open()) SqliteConnection.ClearPool(conn);
        TempDir.Delete(Path.GetDirectoryName(Root)!);
    }
}

internal static class TempDir
{
    /// <summary>A shared-config path that never exists, so the guards never read the developer's real one.</summary>
    public static readonly string NoSharedConfig = Path.Combine(Path.GetTempPath(), "mailvec-devcorpus-no-such-config.json");

    public static string Create(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
