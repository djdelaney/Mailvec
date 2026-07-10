using System.Diagnostics;
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

    // Test seam: lets tests drive RunAsync through a stub executable instead
    // of /bin/launchctl (there is no other way to exercise the pipe-drain and
    // timeout-join behavior against a real subprocess).
    internal string ExecutablePath { get; init; } = "/bin/launchctl";

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

    /// <summary>
    /// Canonical path the installer writes plists to, also the path
    /// <see cref="KickstartAsync"/> hands to <c>launchctl bootstrap</c>.
    /// Visible for testing.
    /// </summary>
    internal static string PlistPath(string label)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "LaunchAgents", $"{label}.plist");
    }

    private static bool PlistExists(string label) => File.Exists(PlistPath(label));

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

        // Fast path: service is already loaded (the post-redeploy restart
        // case). kickstart -k stops and respawns it.
        var (kickExit, _) = await RunAsync(["kickstart", "-k", $"gui/{Uid}/{label}"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (kickExit == 0) return true;

        // Slow path: the service was booted out (the Resume-from-Pause case)
        // or has never been loaded this session. kickstart can't operate on
        // a label that isn't in the registry, so bootstrap the plist back in
        // — RunAtLoad in the plist will spawn the process. If the plist is
        // missing entirely there's nothing we can do; return false so the
        // caller surfaces the failure.
        var plistPath = PlistPath(label);
        if (!File.Exists(plistPath))
        {
            logger.LogWarning(
                "Cannot resume {Label}: kickstart failed (exit {Exit}) and plist {PlistPath} is missing.",
                label, kickExit, plistPath);
            return false;
        }
        var (bootExit, bootOut) = await RunAsync(["bootstrap", $"gui/{Uid}", plistPath], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (bootExit != 0)
        {
            logger.LogWarning(
                "bootstrap of {Label} failed (exit {Exit}): {Stdout}",
                label, bootExit, bootOut.Trim());
        }
        return bootExit == 0;
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

    internal async Task<(int ExitCode, string Stdout)> RunAsync(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc is null) return (-1, string.Empty);

        // Drain BOTH pipes, each into its own task-owned buffer. Stderr was
        // redirected but never read: a child writing more than the ~64KB pipe
        // buffer to stderr blocks on write and rides the whole timeout into a
        // spurious kill (the classic both-pipes-full deadlock, converted to a
        // stall by the timeout). And a shared StringBuilder returned while an
        // abandoned reader might still append is a race — StringBuilder isn't
        // thread-safe — so each drain returns its own string instead.
        var readStdout = DrainAsync(proc.StandardOutput, ct);
        var readStderr = DrainAsync(proc.StandardError, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            _ = await readStderr.ConfigureAwait(false); // drained for the pipe's sake; content unused
            return (proc.ExitCode, await readStdout.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Hung process — kill and report failure.
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            logger.LogWarning("launchctl {Args} timed out after {Timeout}", string.Join(' ', args), timeout);
            // The kill closes the pipes and the drains finish promptly; join
            // (briefly bounded) so the Process is never disposed under a live
            // reader and partial output isn't torn. Only an unkillable
            // process (uninterruptible sleep) misses the grace — report
            // empty output then rather than wait on it.
            var grace = Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            var finished = await Task.WhenAny(readStdout, grace).ConfigureAwait(false);
            return (-1, ReferenceEquals(finished, readStdout) ? await readStdout.ConfigureAwait(false) : string.Empty);
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return string.Empty; // pipe broke under a kill — partial output isn't worth a throw
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
