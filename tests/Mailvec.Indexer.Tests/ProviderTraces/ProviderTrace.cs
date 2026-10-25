using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mailvec.Indexer.Tests.ProviderTraces;

/// <summary>
/// A provider trace: the Maildir mbsync leaves after each of a sequence of
/// server-side operations, and what the server itself holds at that point.
/// Format and the recording procedure: docs/contributing/provider-traces.md.
/// </summary>
/// <param name="Source">"hand-written" (a belief about the provider) or "recorded" (observed).</param>
/// <param name="Unsynced">Mailboxes the trace's mbsync Patterns leave out; the server oracle ignores them.</param>
internal sealed record ProviderTrace(
    int Version,
    string Provider,
    string Source,
    string Description,
    IReadOnlyList<TraceStep> Steps,
    IReadOnlyList<string>? Unsynced = null,
    TraceRecording? Recorded = null)
{
    public const int CurrentVersion = 1;
    public const string HandWritten = "hand-written";
    public const string RecordedSource = "recorded";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public string Location { get; private init; } = "";

    public static ProviderTrace Load(string directory)
    {
        var trace = JsonSerializer.Deserialize<ProviderTrace>(File.ReadAllText(Path.Combine(directory, "trace.json")), Json)
                    ?? throw new InvalidDataException($"{directory}: empty trace.json");
        trace = trace with { Location = directory };
        trace.Validate();
        return trace;
    }

    public byte[] Blob(string name) => File.ReadAllBytes(Path.Combine(Location, "blobs", name));

    /// <summary>
    /// Refuses a trace that could pass for the wrong reason: a missing blob, a
    /// path that is not &lt;folder&gt;/(cur|new)/&lt;file&gt;, a settled step
    /// with no server state to compare against.
    /// </summary>
    private void Validate()
    {
        void Fail(string why) => throw new InvalidDataException($"{Location}: {why}");

        if (Version != CurrentVersion) Fail($"version {Version}, expected {CurrentVersion}");
        if (Source is not (HandWritten or RecordedSource)) Fail($"source '{Source}'");
        if ((Source == RecordedSource) != (Recorded is not null)) Fail("'recorded' is required exactly when source is recorded");
        if (Steps.Count == 0) Fail("no steps");
        if (Steps.Select(s => s.Id).Distinct().Count() != Steps.Count) Fail("duplicate step ids");

        foreach (var step in Steps)
        {
            if (step.Settled && step.Server is null) Fail($"{step.Id}: a settled step needs its server state");
            if (!step.Settled && step.Divergence is not null) Fail($"{step.Id}: a divergence is only meaningful on a settled step");
            foreach (var (path, blob) in step.Maildir)
            {
                var parts = path.Split('/');
                if (parts.Length < 3 || parts[^2] is not ("cur" or "new") || parts.Any(p => p.Length == 0 || p is "." or ".."))
                    Fail($"{step.Id}: '{path}' is not <folder>/(cur|new)/<file>");
                if (!File.Exists(Path.Combine(Location, "blobs", blob))) Fail($"{step.Id}: blob '{blob}' missing");
            }
        }
    }
}

/// <param name="ServerAction">What happened on the server (or locally) to produce this step.</param>
/// <param name="Settled">
/// False for a state mbsync leaves mid-run or between a local change and the
/// next sync: the Maildir oracle still applies, the server oracle does not.
/// </param>
/// <param name="Maildir">Every message file after the step: Maildir-relative path → blob name.</param>
/// <param name="Server">The server's own view: Message-ID (no angle brackets) → state.</param>
/// <param name="Divergence">
/// A known way Mailvec's view differs from the server's on this settled step,
/// and why. The harness requires the divergence to still be there, so a fix
/// cannot leave a stale note behind.
/// </param>
internal sealed record TraceStep(
    string Id,
    string ServerAction,
    IReadOnlyDictionary<string, string> Maildir,
    IReadOnlyDictionary<string, ServerMessage>? Server = null,
    bool Settled = true,
    string? Divergence = null);

/// <param name="Mailboxes">Every mailbox (Gmail: label) that holds the message.</param>
internal sealed record ServerMessage(IReadOnlyList<string> Mailboxes, bool Seen);

/// <param name="Mbsync">The isync version that produced the Maildir, e.g. "1.5.0".</param>
internal sealed record TraceRecording(DateOnly At, string Mbsync, string? Notes = null);

/// <summary>
/// Writes successive steps of a trace into one Maildir, the way mbsync
/// changes it between scans: a path present in both steps is left untouched
/// (its mtime too, so the scanner's fast path sees it exactly as it would),
/// a vanished path is deleted, a new path is written.
/// </summary>
internal sealed class TraceMaildir(string root, ProviderTrace trace)
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private int _step;

    public void Apply(TraceStep step)
    {
        _step++;
        foreach (var (path, _) in _current.Where(kv => !step.Maildir.ContainsKey(kv.Key)))
            File.Delete(Full(path));

        foreach (var (path, blob) in step.Maildir)
        {
            if (_current.TryGetValue(path, out var had) && had == blob) continue;
            var full = Full(path);
            var folder = Path.GetDirectoryName(Path.GetDirectoryName(full)!)!;
            foreach (var sub in (string[])["cur", "new", "tmp"]) Directory.CreateDirectory(Path.Combine(folder, sub));
            File.WriteAllBytes(full, trace.Blob(blob));
            // A distinct, increasing mtime per step: what a real delivery looks like.
            File.SetLastWriteTimeUtc(full, Epoch.AddMinutes(_step));
        }
        _current = new Dictionary<string, string>(step.Maildir, StringComparer.Ordinal);
    }

    private string Full(string path) => Path.Combine([root, .. path.Split('/')]);
}

/// <summary>What Mailvec's index must say about one message.</summary>
internal sealed record ExpectedMessage(IReadOnlySet<string> Folders, bool Read);

internal static class Oracles
{
    /// <summary>
    /// What the Maildir alone implies, applying Mailvec's own rules: one
    /// message per Message-ID, a member of every folder holding a copy, read
    /// only when no copy is unseen. Holds on every step, settled or not.
    /// </summary>
    public static Dictionary<string, ExpectedMessage> FromMaildir(ProviderTrace trace, TraceStep step)
    {
        var copies = step.Maildir
            .Select(kv => (Id: MessageId(trace.Blob(kv.Value)), Path: kv.Key))
            .GroupBy(c => c.Id, StringComparer.Ordinal);
        return copies.ToDictionary(
            g => g.Key,
            g => new ExpectedMessage(
                g.Select(c => FolderOf(c.Path)).ToHashSet(StringComparer.Ordinal),
                g.All(c => Mailvec.Core.Data.MaildirFlags.IsSeen(c.Path))),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// What the server holds, restricted to the mailboxes the trace syncs. A
    /// message left in no synced mailbox is not expected at all.
    /// </summary>
    public static Dictionary<string, ExpectedMessage> FromServer(ProviderTrace trace, TraceStep step)
    {
        var unsynced = (trace.Unsynced ?? []).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, ExpectedMessage>(StringComparer.Ordinal);
        foreach (var (id, state) in step.Server!)
        {
            var folders = state.Mailboxes.Where(m => !unsynced.Contains(m)).ToHashSet(StringComparer.Ordinal);
            if (folders.Count > 0) result[id] = new ExpectedMessage(folders, state.Seen);
        }
        return result;
    }

    private static string FolderOf(string path) => string.Join('/', path.Split('/')[..^2]);

    /// <summary>The Message-ID header without angle brackets, unfolded; enough for test fixtures.</summary>
    internal static string MessageId(byte[] eml)
    {
        var text = Encoding.UTF8.GetString(eml).Replace("\r\n", "\n");
        var headerEnd = text.IndexOf("\n\n", StringComparison.Ordinal);
        var headers = (headerEnd < 0 ? text : text[..headerEnd]).Replace("\n ", " ").Replace("\n\t", " ");
        foreach (var line in headers.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Message-ID", StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim().Trim('<', '>');
        }
        throw new InvalidDataException("fixture blob has no Message-ID");
    }
}
