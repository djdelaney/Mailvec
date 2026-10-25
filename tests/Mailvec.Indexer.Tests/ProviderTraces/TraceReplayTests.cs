using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Indexer.Services;
using Mailvec.Parsing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mailvec.Indexer.Tests.ProviderTraces;

/// <summary>
/// Replays every provider trace through the real scanner, one step at a time
/// into the same Maildir and database, and holds Mailvec's index to two
/// oracles: what the Maildir implies (every step) and what the server holds
/// (settled steps). See docs/contributing/provider-traces.md.
/// </summary>
public sealed class TraceReplayTests
{
    private static readonly string TracesRoot = Path.Combine(AppContext.BaseDirectory, "ProviderTraces");

    public static TheoryData<string> Traces()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(TracesRoot, "trace.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            data.Add(Path.GetRelativePath(TracesRoot, Path.GetDirectoryName(file)!).Replace(Path.DirectorySeparatorChar, '/'));
        return data;
    }

    [Fact]
    public void There_are_traces_to_replay() => Traces().Count.ShouldBeGreaterThan(0);

    [Theory]
    [MemberData(nameof(Traces))]
    public void The_index_agrees_with_the_maildir_and_the_server_at_every_step(string name)
    {
        var trace = ProviderTrace.Load(Path.Combine(TracesRoot, name));
        using var env = new ReplayEnv();
        var maildir = new TraceMaildir(env.Root, trace);

        foreach (var step in trace.Steps)
        {
            var at = $"{name} @ {step.Id}";
            maildir.Apply(step);

            var scan = env.Scanner.ScanAll();
            scan.FailedToParse.ShouldBe(0, at);
            scan.Incomplete.ShouldBeFalse(at);
            scan.DeletionsHeld.ShouldBe(0, at);

            CheckAgainstOracles(env, trace, step, at);

            // A repoint clears the survivor's hash on purpose, so the next scan
            // may re-parse it once. After that, nothing: the zero-write rule
            // has to hold with N copies of every message too.
            env.Scanner.ScanAll().SoftDeleted.ShouldBe(0, $"{at}: the convergence scan");
            var quiet = env.Scanner.ScanAll();
            quiet.Upserted.ShouldBe(0, $"{at}: a scan after convergence must write nothing");
            quiet.SoftDeleted.ShouldBe(0, at);
            env.Scanner.LastScanTransactionsBegun.ShouldBe(0, $"{at}: a scan after convergence must not take the writer lock");

            CheckAgainstOracles(env, trace, step, $"{at} (after convergence)");
        }
    }

    private static void CheckAgainstOracles(ReplayEnv env, ProviderTrace trace, TraceStep step, string at)
    {
        var index = env.Index();

        Differences(Oracles.FromMaildir(trace, step), index.Messages)
            .ShouldBeEmpty($"{at}: the index disagrees with the Maildir");

        if (step.Settled)
        {
            var server = Differences(Oracles.FromServer(trace, step), index.Messages);
            if (step.Divergence is null)
                server.ShouldBeEmpty($"{at}: the index disagrees with the server");
            else
                server.ShouldNotBeEmpty($"{at}: the recorded divergence ({step.Divergence}) no longer happens — remove it from the trace");
        }

        // Attribution: the folder a message is shown in is one it is in, and
        // the file the row points at is there. view_attachment and the OCR
        // pass read that file.
        foreach (var (id, row) in index.Rows)
        {
            index.Messages[id].Folders.ShouldContain(row.Folder, $"{at}: {id} is attributed to a folder it is not in");
            File.Exists(Path.Combine(env.Root, row.MaildirPath, row.MaildirFilename))
                .ShouldBeTrue($"{at}: {id} points at {row.MaildirPath}/{row.MaildirFilename}, which is gone");
        }
    }

    private static List<string> Differences(
        IReadOnlyDictionary<string, ExpectedMessage> expected, IReadOnlyDictionary<string, ExpectedMessage> actual)
    {
        var diffs = new List<string>();
        foreach (var (id, want) in expected)
        {
            if (!actual.TryGetValue(id, out var got)) { diffs.Add($"{id}: expected live, not in the index"); continue; }
            if (!want.Folders.SetEquals(got.Folders))
                diffs.Add($"{id}: folders [{string.Join(", ", want.Folders.Order())}] expected, [{string.Join(", ", got.Folders.Order())}] indexed");
            if (want.Read != got.Read) diffs.Add($"{id}: read={want.Read} expected, read={got.Read} indexed");
        }
        foreach (var id in actual.Keys.Where(k => !expected.ContainsKey(k)))
            diffs.Add($"{id}: live in the index, not expected");
        return diffs;
    }

    private sealed record IndexedRow(string Folder, string MaildirPath, string MaildirFilename);

    private sealed record IndexView(
        IReadOnlyDictionary<string, ExpectedMessage> Messages,
        IReadOnlyDictionary<string, IndexedRow> Rows);

    private sealed class ReplayEnv : IDisposable
    {
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "mailvec-trace-" + Guid.NewGuid().ToString("N"));
        private readonly ConnectionFactory _connections;
        private readonly MessageRepository _messages;

        public string Root { get; }
        public MaildirScanner Scanner { get; }

        public ReplayEnv()
        {
            Root = Path.Combine(_temp, "Mail");
            Directory.CreateDirectory(Root);
            _connections = new ConnectionFactory(Microsoft.Extensions.Options.Options.Create(new ArchiveOptions
            {
                DatabasePath = Path.Combine(_temp, "archive.sqlite"),
            }));
            new SchemaMigrator(_connections, NullLogger<SchemaMigrator>.Instance).EnsureUpToDate();
            _messages = new MessageRepository(_connections);
            Scanner = new MaildirScanner(
                Microsoft.Extensions.Options.Options.Create(new IngestOptions { MaildirRoot = Root }),
                new InProcessParser(extractor: null),
                _messages,
                new ChunkRepository(_connections),
                new SyncStateRepository(_connections),
                _connections,
                NullLogger<MaildirScanner>.Instance);
        }

        /// <summary>Every live message as the index sees it: folder membership, read state, attribution.</summary>
        public IndexView Index()
        {
            var rows = new Dictionary<string, IndexedRow>(StringComparer.Ordinal);
            var folders = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            using (var conn = _connections.Open())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT message_id, folder, maildir_path, maildir_filename FROM messages WHERE deleted_at IS NULL";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) rows[r.GetString(0)] = new IndexedRow(r.GetString(1), r.GetString(2), r.GetString(3));
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT message_id, folder FROM sync_state WHERE message_id IS NOT NULL";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var id = r.GetString(0);
                        if (!folders.TryGetValue(id, out var set)) folders[id] = set = new HashSet<string>(StringComparer.Ordinal);
                        set.Add(r.GetString(1));
                    }
                }
            }
            var read = _messages.GetReadStates(rows.Keys.ToList());
            var messages = rows.Keys.ToDictionary(
                id => id,
                id => new ExpectedMessage(folders.GetValueOrDefault(id) ?? [], read.GetValueOrDefault(id)),
                StringComparer.Ordinal);
            return new IndexView(messages, rows);
        }

        public void Dispose()
        {
            using (var conn = _connections.Open()) SqliteConnection.ClearPool(conn);
            try { Directory.Delete(_temp, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }
}
