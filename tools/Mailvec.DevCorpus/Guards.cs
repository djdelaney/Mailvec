using System.Text.Json;

namespace Mailvec.DevCorpus;

public sealed class RefusedException(string message) : Exception(message);

/// <summary>
/// Refuses any target that could put invented mail where a real pipeline
/// would ingest it. On the dev Mac that is the frozen corpus: an indexer run
/// over a Maildir that gained a few hundred synthetic messages is exactly the
/// silent drift the frozen-corpus guard exists to prevent — and like that
/// guard, this is enforcement, not advice.
/// </summary>
internal static class Guards
{
    /// <summary>
    /// Validates <paramref name="target"/> and returns its resolved full path.
    /// <paramref name="sharedConfigPath"/> is the Mailvec shared config to read
    /// the live Maildir and database locations from (a test can point it elsewhere).
    /// </summary>
    public static string Check(string target, string sharedConfigPath)
    {
        var full = Path.GetFullPath(target);
        if (File.Exists(full)) throw new RefusedException($"{full} exists and is a file.");
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            throw new RefusedException($"{full} is not empty. The generator only writes into a new or empty directory.");

        var parent = Path.GetDirectoryName(full) ?? throw new RefusedException("A filesystem root is not a valid target.");
        if (!Directory.Exists(parent)) throw new RefusedException($"The parent directory {parent} does not exist.");

        // Resolve symlinks in the parent chain, so a link can't disguise
        // where the corpus actually lands.
        var resolved = Path.Combine(RealPath(parent), Path.GetFileName(full));

        for (var dir = Path.GetDirectoryName(resolved); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (IsMaildirFolder(dir))
                throw new RefusedException($"{dir} is a Maildir folder (it has cur/ or new/). Generate somewhere no indexer scans.");
            if (Directory.EnumerateDirectories(dir).Any(IsMaildirFolder))
                throw new RefusedException($"{dir} is a Maildir root (a child folder has cur/ or new/). An indexer scanning it would ingest the corpus.");
            foreach (var marker in (string[])[".mbsyncstate", ".mailvec-mbsync-heartbeat", ".mailvec-mbsync-sync", "archive.sqlite", ".frozen-corpus"])
            {
                if (File.Exists(Path.Combine(dir, marker)))
                    throw new RefusedException($"{dir} contains {marker}: it belongs to a real mail setup.");
            }
        }

        foreach (var live in LiveLocations(sharedConfigPath))
        {
            if (IsWithin(resolved, live))
                throw new RefusedException($"{resolved} is inside {live}, which the Mailvec shared config ({sharedConfigPath}) names as a live location.");
        }
        return resolved;
    }

    /// <summary>The default location of the shared config every Mailvec binary reads.</summary>
    public static string DefaultSharedConfigPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Mailvec", "appsettings.Local.json");

    private static bool IsMaildirFolder(string dir) =>
        Directory.Exists(Path.Combine(dir, "cur")) || Directory.Exists(Path.Combine(dir, "new"));

    /// <summary>
    /// The shared config's directory, its Ingest:MaildirRoot, and the
    /// directory holding its Archive:DatabasePath. An unreadable config is a
    /// refusal, not a pass: it is the one file that knows where the real mail is.
    /// </summary>
    private static IEnumerable<string> LiveLocations(string sharedConfigPath)
    {
        if (!File.Exists(sharedConfigPath)) yield break;
        yield return Path.GetDirectoryName(Path.GetFullPath(sharedConfigPath))!;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(sharedConfigPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new RefusedException($"Cannot read {sharedConfigPath} ({ex.Message}), so cannot tell where the live Maildir is.");
        }
        using (doc)
        {
            if (Get(doc.RootElement, "Ingest", "MaildirRoot") is { } root) yield return Expand(root);
            if (Get(doc.RootElement, "Archive", "DatabasePath") is { } db && Path.GetDirectoryName(Expand(db)) is { } dbDir)
                yield return dbDir;
        }
    }

    private static string? Get(JsonElement root, string section, string key) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object
        && s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string Expand(string path) =>
        Path.GetFullPath(path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path);

    private static bool IsWithin(string path, string dir)
    {
        var d = Path.TrimEndingDirectorySeparator(RealPathIfExists(dir));
        return path == d || path.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string RealPathIfExists(string path) => Directory.Exists(path) ? RealPath(path) : path;

    /// <summary>The path with every symlinked component resolved (an existing directory).</summary>
    private static string RealPath(string existingDir)
    {
        var parts = Path.GetFullPath(existingDir).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = Path.GetPathRoot(Path.GetFullPath(existingDir))!;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            var info = new DirectoryInfo(next);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                // Restart from the resolved target: it may itself contain links.
                return RealPath(Path.Combine([target.FullName, .. parts[(i + 1)..]]));
            }
            current = next;
        }
        return current;
    }
}
