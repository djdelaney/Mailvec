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

    /// <summary>
    /// Poll the pulses channel until one arrives or the deadline elapses,
    /// returning the elapsed wait so failure messages are diagnosable. Beats
    /// a bare <c>ReadAsync(cts.Token)</c> because the failure mode is "no
    /// pulse within budget" rather than an opaque <see cref="OperationCanceledException"/>.
    /// </summary>
    private static async Task<bool> WaitForPulseAsync(MaildirWatcher w, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (w.Pulses.TryRead(out _)) return true;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                if (await w.Pulses.WaitToReadAsync(cts.Token).ConfigureAwait(false))
                {
                    if (w.Pulses.TryRead(out _)) return true;
                }
            }
            catch (OperationCanceledException) { /* loop again */ }
        }
        return false;
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
        // after the configured quiet period. WaitForPulseAsync polls up to
        // a generous budget so slow CI nodes don't false-fail, but reports a
        // diagnosable boolean instead of an opaque OperationCanceledException.
        using var watcher = BuildWatcher(_root, debounceMs: 100);
        watcher.Start();

        // Touch a .eml file inside cur/ — counts as a real change.
        File.WriteAllText(Path.Combine(_root, "INBOX", "cur", "1.host:2,S"), "Subject: hi\n\nbody");

        var pulsed = await WaitForPulseAsync(watcher, TimeSpan.FromSeconds(5));
        pulsed.ShouldBeTrue($"watcher did not pulse within budget; root was '{_root}'");
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
        var pulsed = await WaitForPulseAsync(watcher, TimeSpan.FromMilliseconds(500));
        pulsed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("INBOX/cur/1.host:2,S", false)]
    [InlineData("INBOX/new/2.host", false)]
    [InlineData("INBOX/tmp/partial", true)]
    [InlineData("INBOX/tmp", true)]
    [InlineData("INBOX.Drafts/tmp/draft", true)]
    [InlineData("tmpfolder/cur/notes", false)]       // segment "tmp" must match exactly
    [InlineData("INBOX/cur/tmp-file.eml", false)]    // "tmp" inside a filename is fine
    public void IsInsideMbsyncTmp_matches_only_tmp_path_segments(string relative, bool expected)
    {
        // Regression: the original substring check on "/tmp/" would false-fire
        // whenever the watcher root lived under a path containing /tmp/
        // (macOS $TMPDIR=/tmp/<user>/ during tests). Match against the path
        // *relative to the root* so only true mbsync staging dirs are filtered.
        var rootsUnderTmp = new[]
        {
            "/tmp/claude-501/run",   // the case that flaked this test on macOS
            "/var/folders/zz/T",     // the normal macOS tmpdir
            "/home/user/Mail",       // a non-tmp root
        };
        foreach (var root in rootsUnderTmp)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            MaildirWatcher.IsInsideMbsyncTmp(full, root).ShouldBe(expected,
                $"root='{root}' full='{full}' expected={expected}");
        }
    }

    [Fact]
    public async Task Start_survives_watcher_creation_failure_and_recovers_on_retry()
    {
        // Linux inotify exhaustion surfaces as IOException out of FSW
        // creation. Both Start() call sites sit on the indexer's spine — an
        // escaping throw stops the host, and launchd/Docker restarts it into
        // the same condition: a crash loop through a full rescan each time.
        // Start must swallow, leave scanning to the periodic timer, and let
        // the timer-tick retry bring the watcher up once the pressure clears.
        using var watcher = BuildWatcher(_root, debounceMs: 100);
        watcher.CreateWatcher = _ => throw new IOException("inotify watch limit reached");

        Should.NotThrow(() => watcher.Start());

        // The pressure clears; the next timer tick's Start() succeeds and
        // event-driven pulses work.
        watcher.CreateWatcher = root => new FileSystemWatcher(root);
        Should.NotThrow(() => watcher.Start());
        File.WriteAllText(Path.Combine(_root, "INBOX", "cur", "9.host:2,S"), "Subject: hi\n\nbody");
        (await WaitForPulseAsync(watcher, TimeSpan.FromSeconds(5))).ShouldBeTrue();
    }

    [Fact]
    public async Task Errored_watcher_is_retired_so_the_next_Start_recreates_it()
    {
        // FSW's Error event can mean a permanently dead watcher (deleted or
        // remounted watch root — often with no further events, ever). The
        // old handler logged + pulsed but left _fsw set, so the timer-tick
        // Start() retry no-op'd forever and event-driven scanning silently
        // died, degrading to timer-only coverage.
        using var watcher = BuildWatcher(_root, debounceMs: 100);
        FileSystemWatcher? first = null;
        watcher.CreateWatcher = root => first = new FileSystemWatcher(root);
        watcher.Start();
        first.ShouldNotBeNull();

        watcher.HandleWatcherError(first!, new IOException("watch root vanished"));

        // The error forces an immediate full-pass pulse for the dropped events...
        (await WaitForPulseAsync(watcher, TimeSpan.FromSeconds(2))).ShouldBeTrue();

        // ...and the next timer tick's Start() recreates instead of no-opping.
        FileSystemWatcher? second = null;
        watcher.CreateWatcher = root => second = new FileSystemWatcher(root);
        watcher.Start();
        second.ShouldNotBeNull();

        // Event-driven pulses flow through the replacement.
        File.WriteAllText(Path.Combine(_root, "INBOX", "cur", "10.host:2,S"), "Subject: hi\n\nbody");
        (await WaitForPulseAsync(watcher, TimeSpan.FromSeconds(5))).ShouldBeTrue();
    }

    [Fact]
    public void A_stale_instances_late_error_does_not_tear_down_its_replacement()
    {
        using var watcher = BuildWatcher(_root, debounceMs: 100);
        FileSystemWatcher? first = null;
        watcher.CreateWatcher = root => first = new FileSystemWatcher(root);
        watcher.Start();
        watcher.HandleWatcherError(first!, new IOException("dead"));

        FileSystemWatcher? second = null;
        watcher.CreateWatcher = root => second = new FileSystemWatcher(root);
        watcher.Start();
        second.ShouldNotBeNull();

        // The first instance errors again, late — the replacement must survive.
        watcher.HandleWatcherError(first!, new IOException("late error from retired instance"));

        FileSystemWatcher? third = null;
        watcher.CreateWatcher = root => third = new FileSystemWatcher(root);
        watcher.Start();
        third.ShouldBeNull(); // Start no-ops: the second instance is still active
    }

    [Fact]
    public async Task Negative_debounce_config_is_clamped_and_pulses_still_flow()
    {
        // A negative DebounceMilliseconds used to make Task.Delay throw
        // inside the unobserved debounce task, leaving _debounceTask
        // permanently non-null: every future event saw a "running" loop and
        // no pulse ever fired again — a silent, timer-covered death of
        // event-driven scanning. The constructor clamps to >= 0 now.
        using var watcher = BuildWatcher(_root, debounceMs: -250);
        watcher.Start();

        File.WriteAllText(Path.Combine(_root, "INBOX", "cur", "11.host:2,S"), "Subject: hi\n\nbody");

        (await WaitForPulseAsync(watcher, TimeSpan.FromSeconds(5))).ShouldBeTrue();
    }

    [Fact]
    public async Task Rapid_event_bursts_coalesce_into_one_debounced_pulse()
    {
        // mbsync delivers rename bursts; each event landing inside the quiet
        // window must extend the debounce (the loop-back path) rather than
        // pulse per event. Generous timings so slow CI nodes don't flake:
        // the gaps sit well inside the debounce window.
        using var watcher = BuildWatcher(_root, debounceMs: 500);
        watcher.Start();

        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_root, "INBOX", "cur", $"burst-{i}.host:2,S"), "Subject: hi\n\nbody");
            await Task.Delay(50);
        }

        (await WaitForPulseAsync(watcher, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        // The burst coalesced — no second pulse trails it.
        (await WaitForPulseAsync(watcher, TimeSpan.FromMilliseconds(800))).ShouldBeFalse();
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
