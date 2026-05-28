using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Mailvec.Core.Tray;

/// <summary>
/// Thin wrapper around <c>launchctl print gui/&lt;uid&gt;/&lt;label&gt;</c> for
/// the four mailvec agents (mbsync / indexer / embedder / mcp). The tray
/// dashboard's "services" tiles + "last sync" banner read from this; the
/// preferences "service controls" buttons call <see cref="KickstartAsync"/>
/// and <see cref="BootoutAsync"/>.
///
/// We deliberately shell out instead of binding to a launchctl C API: the
/// supported XPC API is private, and shelling is what every other macOS
/// status-bar app does for the same job. The output format is stable
/// enough across macOS versions that a regex-based parser is fine.
/// </summary>
public sealed class LaunchdInspector(ILogger<LaunchdInspector> logger)
{
    public static readonly IReadOnlyList<string> ServiceLabels =
    [
        "com.mailvec.mbsync",
        "com.mailvec.indexer",
        "com.mailvec.embedder",
        "com.mailvec.mcp",
    ];

    private static readonly TimeSpan PrintTimeout = TimeSpan.FromSeconds(3);

    private static int Uid => Environment.UserName == "root" ? 0 : (int)getuid();

    public async Task<IReadOnlyDictionary<string, LaunchdServiceInfo>> InspectAllAsync(CancellationToken ct = default)
    {
        var tasks = ServiceLabels.Select(async label =>
        {
            var info = await InspectAsync(label, ct).ConfigureAwait(false);
            return (label, info);
        }).ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToDictionary(t => t.label, t => t.info);
    }

    public async Task<LaunchdServiceInfo> InspectAsync(string label, CancellationToken ct = default)
    {
        var (exitCode, stdout) = await RunAsync(["print", $"gui/{Uid}/{label}"], PrintTimeout, ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            // "bootout" (the Pause button's verb) and "never installed" both
            // make `launchctl print` exit non-zero. Disambiguate by checking
            // for the plist on disk: present means installed-but-paused;
            // absent means never installed. ClassifyService keys off
            // State == "paused" to skip the red error banner.
            var state = PlistExists(label) ? "paused" : "unloaded";
            return new LaunchdServiceInfo(label, Loaded: false, State: state, Pid: null, LastExitCode: null, Runs: 0);
        }

        return ParsePrintOutput(label, stdout);
    }

    private static bool PlistExists(string label)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return File.Exists(Path.Combine(home, "Library", "LaunchAgents", $"{label}.plist"));
    }

    /// <summary>
    /// Visible for testing — given the raw text from
    /// <c>launchctl print gui/&lt;uid&gt;/&lt;label&gt;</c>, parse the fields
    /// the tray actually uses. Robust to extra fields and indentation changes.
    /// </summary>
    public static LaunchdServiceInfo ParsePrintOutput(string label, string text)
    {
        var state = Extract(text, "state") ?? "unknown";
        int? pid = int.TryParse(Extract(text, "pid"), out var p) ? p : null;
        int? lastExit = int.TryParse(Extract(text, "last exit code"), out var ec) ? ec : null;
        int.TryParse(Extract(text, "runs"), out var runs);

        return new LaunchdServiceInfo(
            Label: label,
            Loaded: true,
            State: state,
            Pid: pid,
            LastExitCode: lastExit,
            Runs: runs);
    }

    private static string? Extract(string text, string key)
    {
        // Lines look like:    \tkey = value
        // Some keys (e.g. "active count") contain spaces; require a leading
        // whitespace + the exact key + ' = '. Returns the trimmed value or null.
        var needle = $"\n\t{key} = ";
        var idx = text.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 0)
        {
            // First line of the block doesn't have a preceding newline. Tolerate.
            var alt = $"{key} = ";
            idx = text.IndexOf(alt, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += alt.Length;
        }
        else
        {
            idx += needle.Length;
        }

        var end = text.IndexOf('\n', idx);
        var value = end < 0 ? text[idx..] : text[idx..end];
        return value.Trim();
    }

    public async Task<bool> KickstartAsync(string label, CancellationToken ct = default)
    {
        if (!ServiceLabels.Contains(label))
        {
            throw new ArgumentException($"Unknown service label: {label}");
        }
        var (exit, _) = await RunAsync(["kickstart", "-k", $"gui/{Uid}/{label}"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return exit == 0;
    }

    public async Task<bool> BootoutAsync(string label, CancellationToken ct = default)
    {
        if (!ServiceLabels.Contains(label))
        {
            throw new ArgumentException($"Unknown service label: {label}");
        }
        var (exit, _) = await RunAsync(["bootout", $"gui/{Uid}/{label}"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return exit == 0;
    }

    private async Task<(int ExitCode, string Stdout)> RunAsync(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("/bin/launchctl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc is null) return (-1, string.Empty);

        var stdout = new StringBuilder();
        var readStdout = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                stdout.AppendLine(line);
            }
        }, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await readStdout.ConfigureAwait(false);
            return (proc.ExitCode, stdout.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Hung process — kill and report failure.
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            logger.LogWarning("launchctl {Args} timed out after {Timeout}", string.Join(' ', args), timeout);
            return (-1, stdout.ToString());
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
    private static extern uint getuid();
}

public sealed record LaunchdServiceInfo(
    string Label,
    bool Loaded,
    string State,            // "running" | "not running" | "spawn scheduled" | "unloaded" | "unknown"
    int? Pid,
    int? LastExitCode,
    int Runs);
