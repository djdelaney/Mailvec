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
}
