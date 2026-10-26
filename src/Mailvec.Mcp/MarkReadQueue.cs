using System.Globalization;
using System.Text;
using Mailvec.Core;
using Mailvec.Core.Health;
using Mailvec.Core.Options;
using Microsoft.Extensions.Options;

namespace Mailvec.Mcp;

/// <summary>
/// mcp's half of the optional <c>mark_read</c> component: it reads the
/// sidecar's applier marker and writes request files. It never touches the
/// Maildir. The sidecar (<c>mbsync-mark-read</c> in the Dockerfile) is the
/// consumer and trusts none of what is written here — it re-validates every
/// line against its own allowlist. See docs/proposals/read-state.md.
///
/// <para><b>Both formats are a contract with a POSIX sh script on Alpine</b>,
/// which is why they are line-oriented and versioned by a header line rather
/// than JSON: the consumer has no <c>jq</c>. Change either side and change
/// the other; <c>ops/tests/test_mbsync_mark_read.py</c> exercises the sidecar
/// against files in exactly this shape.</para>
/// </summary>
public sealed class MarkReadQueue(IOptions<McpOptions> mcp, IOptions<IngestOptions> ingest)
{
    /// <summary>Written by the sidecar on its beat timer; read here.</summary>
    public const string ApplierFileName = ".applier";
    public const string ApplierHeader = "mailvec-mark-read-applier v1";
    public const string RequestHeader = "mailvec-mark-read v1";
    public const string RequestExtension = ".req";
    /// <summary>The allowlist entry that means every folder — never implied by an empty list.</summary>
    public const string AllFolders = "*";

    private readonly string _queue = mcp.Value.MarkRead.QueueDirectory;

    /// <summary>
    /// Whether the sidecar is applying requests right now, and which folders
    /// it will act on. Judged like every other beat in the stack: stale at
    /// <see cref="ServiceHeartbeat.StaleAfterMissedBeats"/> × the cadence the
    /// marker itself declares. Missing, stale or malformed all mean the same
    /// thing to a caller — nothing written now would be applied — so all
    /// three are unhealthy, and <c>mark_read</c> writes nothing.
    /// </summary>
    public ApplierState ReadApplier(DateTimeOffset now)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(Path.Combine(_queue, ApplierFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ApplierState.Unhealthy("the mark-read applier has not reported (is the mbsync sidecar running with MBSYNC_MARK_READ_QUEUE?)");
        }

        if (lines.Length < 3 || lines[0] != ApplierHeader
            || !DateTimeOffset.TryParse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            || !int.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out var cadence)
            || cadence < 1)
        {
            return ApplierState.Unhealthy("the mark-read applier marker is malformed");
        }

        if (now - at > TimeSpan.FromSeconds((double)cadence * ServiceHeartbeat.StaleAfterMissedBeats))
            return ApplierState.Unhealthy("the mark-read applier has stopped reporting (is the mbsync sidecar running?)");

        var folders = lines.Skip(3).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (folders.Count == 0)
            return ApplierState.Unhealthy("the mark-read applier publishes no allowed folders");
        return new ApplierState(true, null, folders.Contains(AllFolders) ? null : folders);
    }

    /// <summary>
    /// The request line's location fields for one copy: the folder directory
    /// relative to the Maildir root (<c>.</c> for the root itself) and the
    /// file's base name without its <c>:2,</c> info suffix — the flags are
    /// exactly what changes, so a full name would go stale on the next pull.
    /// Null when the path is not a <c>cur</c>/<c>new</c> file strictly under
    /// the configured root: a row like that is bad data, not a request.
    /// </summary>
    public RequestTarget? TargetFor(string maildirFullPath)
    {
        var root = PathExpansion.Expand(ingest.Value.MaildirRoot);
        var full = Path.GetFullPath(maildirFullPath);
        var rel = Path.GetRelativePath(root, full);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;

        var parts = rel.Split(Path.DirectorySeparatorChar);
        if (parts.Length < 2 || parts[^2] is not ("cur" or "new")) return null;

        var name = parts[^1];
        var info = name.IndexOf(":2,", StringComparison.Ordinal);
        var baseName = info >= 0 ? name[..info] : name;
        var dir = parts.Length == 2 ? "." : string.Join('/', parts[..^2]);
        return new RequestTarget(dir, baseName);
    }

    /// <summary>
    /// Writes one request file atomically (temp name, then rename), so the
    /// sidecar's <c>*.req</c> glob never sees a partial file. Returns its name.
    /// </summary>
    public string Write(IReadOnlyList<RequestLine> lines, DateTimeOffset now)
    {
        var body = new StringBuilder(RequestHeader).Append('\n');
        foreach (var l in lines)
            body.Append(l.Read ? "read" : "unread").Append('\t').Append(l.Target.Dir).Append('\t').Append(l.Target.BaseName).Append('\n');

        // Name order is apply order (the sidecar sorts), so a timestamp prefix
        // makes a later `unread` beat an earlier `read` for the same message.
        // Strictly increasing per process: two calls in one millisecond would
        // otherwise tie on the stamp and sort by the random suffix.
        var name = NextStamp(now).ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N") + RequestExtension;
        // Owner-only: a request names a folder and a Maildir filename per
        // message, on a host bind mount, and the sidecar reading it runs as
        // the same uid.
        var tmp = Path.Combine(_queue, "." + Guid.NewGuid().ToString("N") + ".tmp");
        using (var fs = OwnerOnlyFile.CreateNew(tmp))
        using (var w = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            w.Write(body.ToString());
        File.Move(tmp, Path.Combine(_queue, name));
        return name;
    }

    private static long s_lastStampMs;

    private static DateTime NextStamp(DateTimeOffset now)
    {
        var want = now.ToUnixTimeMilliseconds();
        long last, next;
        do
        {
            last = Interlocked.Read(ref s_lastStampMs);
            next = Math.Max(want, last + 1);
        } while (Interlocked.CompareExchange(ref s_lastStampMs, next, last) != last);
        return DateTimeOffset.FromUnixTimeMilliseconds(next).UtcDateTime;
    }
}

/// <param name="AllowedFolders">Null means every folder (<c>*</c>).</param>
public sealed record ApplierState(bool Healthy, string? Reason, IReadOnlySet<string>? AllowedFolders)
{
    public static ApplierState Unhealthy(string reason) => new(false, reason, new HashSet<string>());

    public bool Allows(string? folder) =>
        Healthy && folder is not null && (AllowedFolders is null || AllowedFolders.Contains(folder));
}

public sealed record RequestTarget(string Dir, string BaseName);

public sealed record RequestLine(bool Read, RequestTarget Target);
