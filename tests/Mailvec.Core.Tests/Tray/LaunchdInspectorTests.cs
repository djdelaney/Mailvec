using Mailvec.Core.Tray;

namespace Mailvec.Core.Tests.Tray;

/// <summary>
/// Exercises <see cref="LaunchdInspector.ParsePrintOutput"/> against captured
/// fixtures from <c>launchctl print gui/&lt;uid&gt;/&lt;label&gt;</c>. We
/// can't shell out reliably in CI (no agents loaded there), so the parser is
/// the unit-testable surface; the shell-out wrapper is exercised by the
/// integration tests under tests/Mailvec.Mcp.Tests/.
/// </summary>
public sealed class LaunchdInspectorTests
{
    [Fact]
    public void Parse_RunningDaemon_ExtractsPidAndState()
    {
        const string text = """
            gui/501/com.mailvec.mcp = {
            	active count = 1
            	type = LaunchAgent
            	state = running
            	program = /usr/local/share/dotnet/dotnet
            	runs = 3
            	pid = 27150
            	last exit code = 0
            }
            """;

        var info = LaunchdInspector.ParsePrintOutput("com.mailvec.mcp", text);

        Assert.Equal("com.mailvec.mcp", info.Label);
        Assert.True(info.Loaded);
        Assert.Equal("running", info.State);
        Assert.Equal(27150, info.Pid);
        Assert.Equal(0, info.LastExitCode);
        Assert.Equal(3, info.Runs);
    }

    [Fact]
    public void Parse_IdleTimerAgent_OmitsPid()
    {
        const string text = """
            gui/501/com.mailvec.mbsync = {
            	active count = 0
            	type = LaunchAgent
            	state = not running
            	runs = 626
            	last exit code = 0
            }
            """;

        var info = LaunchdInspector.ParsePrintOutput("com.mailvec.mbsync", text);

        Assert.True(info.Loaded);
        Assert.Equal("not running", info.State);
        Assert.Null(info.Pid);
        Assert.Equal(0, info.LastExitCode);
        Assert.Equal(626, info.Runs);
    }

    [Fact]
    public void Parse_CrashedDaemon_CapturesNonZeroExitCode()
    {
        const string text = """
            gui/501/com.mailvec.embedder = {
            	state = not running
            	runs = 12
            	last exit code = 1
            }
            """;

        var info = LaunchdInspector.ParsePrintOutput("com.mailvec.embedder", text);

        Assert.Equal(1, info.LastExitCode);
        Assert.Null(info.Pid);
    }

    // ── RunAsync subprocess behavior (stub executable, not launchctl) ───────

    private static LaunchdInspector BuildWithStub(string scriptBody)
    {
        var script = Path.Combine(Path.GetTempPath(), "mailvec-runasync-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(script, "#!/bin/sh\n" + scriptBody);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new LaunchdInspector(Microsoft.Extensions.Logging.Abstractions.NullLogger<LaunchdInspector>.Instance)
        {
            ExecutablePath = script,
        };
    }

    [Fact]
    public async Task RunAsync_drains_a_chatty_stderr_instead_of_deadlocking()
    {
        if (OperatingSystem.IsWindows()) return; // shell-script stub

        // ~230KB to stderr — well past the ~64KB pipe buffer. Stderr was
        // redirected but never drained, so the child blocked on the full pipe
        // and rode the whole timeout into a spurious kill: exit -1 and
        // "unloaded" tiles on /tray/status instead of the real output.
        var inspector = BuildWithStub("""
            i=0
            while [ $i -lt 3000 ]; do echo "stderr noise line with enough padding to fill the pipe buffer quickly" >&2; i=$((i+1)); done
            echo OK
            exit 0
            """);

        var (exit, stdout) = await inspector.RunAsync([], TimeSpan.FromSeconds(10), default);

        Assert.Equal(0, exit);
        Assert.Equal("OK", stdout.Trim());
    }

    [Fact]
    public async Task RunAsync_timeout_kill_joins_the_reader_and_returns_partial_output()
    {
        if (OperatingSystem.IsWindows()) return; // shell-script stub

        // A hung process gets killed at the timeout. The old code read a
        // shared StringBuilder while the abandoned reader task might still be
        // appending (StringBuilder is not thread-safe) and disposed the
        // Process under it; now the kill closes the pipes, the drain task is
        // joined, and the partial output comes back torn-free.
        var inspector = BuildWithStub("""
            echo PARTIAL
            sleep 60
            """);

        // The timeout budget starts when Process.Start returns, so it has to
        // cover shell spawn + `echo PARTIAL` + flush before the kill fires —
        // the whole point being that partial output emitted pre-kill survives.
        // 1s was too tight on GitHub's macOS runners (subprocess spawn there
        // has a fat tail under parallel-test load): the shell occasionally
        // hadn't reached its first line before the kill, so stdout came back
        // "" and this test flaked. 5s is ample margin for the spawn+flush while
        // still killing the process ~55s before its sleep would end. This value
        // is not the behaviour under test — the kill/drain/join is — so a
        // generous timeout costs nothing but a few seconds on this one test.
        var (exit, stdout) = await inspector.RunAsync([], TimeSpan.FromSeconds(5), default);

        Assert.Equal(-1, exit);
        Assert.Equal("PARTIAL", stdout.Trim());
    }
}
