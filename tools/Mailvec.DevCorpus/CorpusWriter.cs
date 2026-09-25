using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Mailvec.DevCorpus;

/// <summary>
/// Builds the corpus in memory, then writes it:
/// <code>
/// &lt;dir&gt;/Mail/          the Maildir (Ingest__MaildirRoot)
/// &lt;dir&gt;/state/         where archive.sqlite goes (Archive__DatabasePath)
/// &lt;dir&gt;/eval/queries.json   `mailvec eval --queries …`
/// &lt;dir&gt;/manifest.json  every scenario, its files and its expected outcome
/// &lt;dir&gt;/env.sh         the exports that point a dev run at all of the above
/// &lt;dir&gt;/outside/       --hazards only: files beside the Maildir, never in it
/// </code>
/// </summary>
public static class CorpusWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep 東京 and é readable in the files
    };

    /// <summary>Everything the options describe, deterministic, nothing written.</summary>
    public static Corpus Build(CorpusOptions options)
    {
        if (options.Filler < 0) throw new ArgumentOutOfRangeException(nameof(options), "Filler count cannot be negative.");
        if (options.Embedding is { } e && !HostedEmbedding.Names.Contains(e))
            throw new ArgumentException($"Unknown embedding profile '{e}'. Known: {string.Join(", ", HostedEmbedding.Names)}.", nameof(options));
        var catalog = new Catalog();
        var scenarios = catalog.Build();
        var filler = Filler.Build(catalog, options.Filler);
        var hazards = options.Hazards ? Extras.Hazards(catalog) : [];
        return new Corpus(scenarios, filler, Extras.Eval(), hazards);
    }

    /// <summary>Guards the target, then writes the corpus. Returns the resolved corpus directory.</summary>
    public static string Write(string target, CorpusOptions options, string? sharedConfigPath = null)
    {
        var corpus = Build(options); // validates the options before the guards touch the disk
        var root = Guards.Check(target, sharedConfigPath ?? Guards.DefaultSharedConfigPath());

        var mail = Path.Combine(root, "Mail");
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(state);

        foreach (var folder in corpus.AllMailFiles.Select(f => f.Folder).Distinct(StringComparer.Ordinal))
        {
            // mbsync creates all three for every folder.
            foreach (var sub in (string[])["cur", "new", "tmp"])
                Directory.CreateDirectory(Path.Combine(mail, folder, sub));
        }
        foreach (var file in corpus.AllMailFiles)
            File.WriteAllBytes(Path.Combine(mail, file.RelativePath), file.Bytes);

        foreach (var hazard in corpus.Hazards)
        {
            foreach (var (rel, bytes) in hazard.Outside)
            {
                var path = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }
            foreach (var (link, target2) in hazard.Symlinks)
            {
                var linkPath = Path.Combine(mail, link);
                Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
                File.CreateSymbolicLink(linkPath, Path.Combine(root, target2));
            }
        }

        Directory.CreateDirectory(Path.Combine(root, "eval"));
        File.WriteAllText(Path.Combine(root, "eval", "queries.json"), EvalJson(corpus.Eval));
        File.WriteAllText(Path.Combine(root, "manifest.json"), ManifestJson(corpus, options));
        File.WriteAllText(Path.Combine(root, "env.sh"), EnvSh(root, mail, state) + HostedEmbedding.EnvSh(options.Embedding));
        return root;
    }

    private static string EvalJson(IReadOnlyList<EvalQuery> queries) =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            queries = queries.Select(q => new
            {
                id = q.Id,
                query = q.Query,
                filters = q.Folder is null ? null : new { folder = q.Folder },
                relevant = q.Relevant,
                notes = q.Notes,
            }),
        }, Json) + "\n";

    private static string ManifestJson(Corpus corpus, CorpusOptions options) =>
        JsonSerializer.Serialize(new
        {
            generator = "tools/Mailvec.DevCorpus",
            note = "Synthetic mail. Every name, address and document is invented. Expectations are checked by tests/Mailvec.DevCorpus.Tests.",
            filler = options.Filler,
            hazards = options.Hazards,
            embedding = options.Embedding,
            scenarios = corpus.Scenarios.Select(s => new
            {
                s.Id,
                s.Description,
                s.MessageId,
                s.Subject,
                files = s.Files.Select(f => "Mail/" + f.RelativePath),
                s.Expect,
            }),
            hazardCases = corpus.Hazards.Select(h => new
            {
                h.Id,
                h.Description,
                files = h.Files.Select(f => "Mail/" + f.RelativePath)
                    .Concat(h.Outside.Keys)
                    .Concat(h.Symlinks.Select(kv => $"Mail/{kv.Key} -> {kv.Value}")),
            }),
        }, Json) + "\n";

    private static string EnvSh(string root, string mail, string state)
    {
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        var sb = new StringBuilder();
        sb.Append("# Generated by tools/Mailvec.DevCorpus — synthetic mail, safe to delete.\n");
        sb.Append($"# Load it into your shell:  . {Q(Path.Combine(root, "env.sh"))}\n");
        sb.Append("#\n");
        sb.Append("# Environment variables outrank every appsettings file, including the shared\n");
        sb.Append("# one on the dev Mac, so everything run from this shell uses THIS corpus.\n");
        sb.Append("#\n");
        sb.Append("#   dotnet run --project src/Mailvec.Indexer          # Ctrl-C once it logs \"MaildirScanner: seen=...\"\n");
        sb.Append("#   dotnet run --project src/Mailvec.Cli -- status\n");
        sb.Append("#   dotnet run --project src/Mailvec.Cli -- search 'cedar'\n");
        sb.Append("#   dotnet run --project src/Mailvec.Mcp              # MCP at http://127.0.0.1:3333 (root path)\n");
        sb.Append("#   dotnet run --project src/Mailvec.Cli -- eval --queries \"$MAILVEC_DEV_EVAL_QUERIES\"   # needs embeddings\n");
        sb.Append($"export Archive__DatabasePath={Q(Path.Combine(state, "archive.sqlite"))}\n");
        sb.Append($"export Ingest__MaildirRoot={Q(mail)}\n");
        sb.Append($"export MAILVEC_DEV_EVAL_QUERIES={Q(Path.Combine(root, "eval", "queries.json"))}\n");
        sb.Append("# Webmail links would point at a real Fastmail account (on the dev Mac the shared\n");
        sb.Append("# config holds one) for messages that exist nowhere. Empty turns them off.\n");
        sb.Append("export Fastmail__AccountId=''\n");
        return sb.ToString();
    }
}
