using Mailvec.Core;
using Mailvec.Core.Options;
using Microsoft.Extensions.Configuration;

namespace Mailvec.OcrBench;

/// <summary>
/// Reads the same shared config every Mailvec binary reads, so the harness
/// benchmarks against the real archive path, the real Maildir root, and — for
/// the incumbent engine — the real <c>Ollama:*</c> settings the embedder uses.
/// Env vars still win, so a one-off <c>Ollama__VisionModel=…</c> compares two
/// local models without editing anything.
/// </summary>
internal sealed record Config(ArchiveOptions Archive, IngestOptions Ingest, OllamaOptions Ollama)
{
    public static Config Load()
    {
        var configuration = new ConfigurationBuilder()
            .AddMailvecSharedConfig()
            .AddEnvironmentVariables()
            .Build();

        var archive = new ArchiveOptions();
        var ingest = new IngestOptions();
        var ollama = new OllamaOptions();
        configuration.GetSection(ArchiveOptions.SectionName).Bind(archive);
        configuration.GetSection(IngestOptions.SectionName).Bind(ingest);
        configuration.GetSection(OllamaOptions.SectionName).Bind(ollama);

        archive.DatabasePath = PathExpansion.Expand(archive.DatabasePath);
        ingest.MaildirRoot = PathExpansion.Expand(ingest.MaildirRoot);
        return new Config(archive, ingest, ollama);
    }
}

/// <summary>Minimal <c>--flag value</c> parsing. No System.CommandLine — this tool has four commands.</summary>
internal sealed class Args
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; }

    public Args(string[] argv)
    {
        Command = argv.Length > 0 && !argv[0].StartsWith('-') ? argv[0] : "help";

        for (var i = Command == "help" ? 0 : 1; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = argv[i][2..];
            if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                _values[key] = argv[++i];
            }
            else
            {
                _flags.Add(key);
            }
        }
    }

    public string? GetOrNull(string name) => _values.GetValueOrDefault(name);

    public string Get(string name, string fallback) => _values.GetValueOrDefault(name) ?? fallback;

    public string Require(string name) =>
        _values.GetValueOrDefault(name) ?? throw new ArgsException($"--{name} is required.");

    public bool Has(string name) => _flags.Contains(name) || _values.ContainsKey(name);

    public IReadOnlyList<string> GetMany(string name) =>
        _values.TryGetValue(name, out var v) ? v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
}

internal sealed class ArgsException(string message) : Exception(message);
