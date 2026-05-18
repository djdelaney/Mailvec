using Mailvec.Core.Options;
using Mailvec.Indexer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailvec.Indexer.Tests;

public class MaildirWatcherTests : IDisposable
{
    private readonly string _root;

    public MaildirWatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mailvec-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "INBOX", "cur"));
        Directory.CreateDirectory(Path.Combine(_root, "INBOX", "tmp"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void Start_with_missing_root_does_not_throw_and_emits_no_pulses()
    {
        // The watcher tolerates a misconfigured MaildirRoot — it logs a warning
        // and stays inert. Without this, a fresh install where mbsync hasn't
        // populated ~/Mail yet would crash the indexer. Keep the soft-failure.
        var bogus = Path.Combine(_root, "does-not-exist");
        using var watcher = BuildWatcher(bogus);

        Should.NotThrow(() => watcher.Start());
        watcher.Pulses.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task File_creation_in_cur_emits_a_debounced_pulse()
    {
        // FileSystemWatcher fires async; the debounce loop schedules a pulse
        // after the configured quiet period. The test waits up to ~3 seconds
        // for the channel to deliver — generous enough for slow CI runners
        // without hanging the test suite if the watcher is broken.
        using var watcher = BuildWatcher(_root, debounceMs: 100);
        watcher.Start();

        // Touch a .eml file inside cur/ — counts as a real change.
        File.WriteAllText(Path.Combine(_root, "INBOX", "cur", "1.host:2,S"), "Subject: hi\n\nbody");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var got = await watcher.Pulses.ReadAsync(cts.Token);
        got.ShouldBe((byte)0);
    }

    [Fact]
    public async Task Events_inside_tmp_are_filtered_out()
    {
        // mbsync writes to tmp/ before atomically renaming into new/. Treating
        // tmp/ events as scan triggers would burn CPU re-scanning every partial
        // delivery the watcher caught mid-rename. The filter is in
        // MaildirWatcher.OnEvent — verify nothing pulses for a tmp/ write.
        using var watcher = BuildWatcher(_root, debounceMs: 100);
        watcher.Start();

        File.WriteAllText(Path.Combine(_root, "INBOX", "tmp", "partial"), "in-flight");

        // Wait past the debounce; we expect no pulse.
        await Task.Delay(500);
        watcher.Pulses.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void Dispose_closes_the_pulses_channel()
    {
        var watcher = BuildWatcher(_root, debounceMs: 50);
        watcher.Start();
        watcher.Dispose();

        // After completion, the reader signals completion to consumers.
        watcher.Pulses.Completion.IsCompleted.ShouldBeTrue();
    }

    private static MaildirWatcher BuildWatcher(string root, int debounceMs = 200)
    {
        var ingest = Options.Create(new IngestOptions { MaildirRoot = root });
        var indexer = Options.Create(new IndexerOptions { DebounceMilliseconds = debounceMs });
        return new MaildirWatcher(ingest, indexer, NullLogger<MaildirWatcher>.Instance);
    }
}
