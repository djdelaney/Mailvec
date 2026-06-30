using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Mailvec.Core.Options;

/// <summary>
/// Single source of truth for user-configurable settings shared across every
/// Mailvec binary — the indexer, embedder, MCP server (both launchd-installed
/// and MCPB-bundled copies), and the CLI all read from one file at
/// <see cref="SharedConfigPath"/>.
///
/// Why this exists: previously the same five settings (database path,
/// Maildir root, Ollama URL, Fastmail account id, log-tool-calls flag) were
/// duplicated across the launchd plist's EnvironmentVariables AND the MCPB
/// manifest's user_config UI. Users had to enter the same values twice and
/// the two sides could drift silently — most visibly when a Fastmail account
/// id worked in Claude Desktop but the launchd HTTP MCP returned
/// `webmailUrl: null` to Claude Code, the tray, and the CLI.
///
/// The file is gitignored, owned entirely by <c>ops/install.sh</c>. Users
/// can also edit it directly; the host won't reload changes mid-run (we
/// don't pass <c>reloadOnChange</c>) — restart the affected services for
/// the change to take effect.
/// </summary>
public static class SharedConfig
{
    /// <summary>
    /// Absolute home-relative path. Lives alongside <c>archive.sqlite</c>
    /// under Application Support so it follows the same data-locality
    /// convention as the rest of Mailvec.
    /// </summary>
    public const string SharedConfigPath = "~/Library/Application Support/Mailvec/appsettings.Local.json";

    /// <summary>
    /// Adds the shared file to the configuration builder at a precedence
    /// step between the binary-local appsettings and environment variables.
    /// Final order: appsettings.json defaults → shared file → env vars
    /// (highest). That ordering means a developer can still set, e.g.,
    /// <c>Ollama__BaseUrl</c> in their shell for a one-off test without
    /// editing the shared file, and the MCPB bundle's
    /// <c>Mcp__LogToolCalls</c> env var (passed in by Claude Desktop's
    /// user_config UI) keeps working.
    ///
    /// Insertion strategy: anchor to the binary-local appsettings JSON
    /// sources — insert immediately *after* the last <c>appsettings*.json</c>
    /// source so the shared file overrides them, while staying *below* the
    /// environment-variable source (every builder adds env vars after its
    /// JSON sources) so an <c>Ollama__BaseUrl</c> env override still wins.
    ///
    /// Why not "before the first env source" (the original approach):
    /// <c>WebApplication.CreateBuilder</c> seeds an early host-level
    /// <c>EnvironmentVariables</c> source (<c>DOTNET_</c> prefix) *ahead* of
    /// <c>appsettings.json</c>. Inserting before that put the shared file
    /// *below* <c>appsettings.json</c>, so a shared <c>Ollama:BaseUrl</c> was
    /// silently shadowed by the binary's own <c>appsettings.json</c> — the
    /// HTTP MCP (and thus the tray) ignored the shared endpoint while the
    /// embedder appeared to honour it (only because we'd set an env override).
    /// Anchoring to the appsettings source is correct for both
    /// <c>Host.CreateApplicationBuilder</c> and <c>WebApplication.CreateBuilder</c>.
    /// If no appsettings source is present we append, letting the caller add
    /// env vars later (the pattern used by <c>CliServices</c>).
    /// </summary>
    public static IConfigurationBuilder AddMailvecSharedConfig(this IConfigurationBuilder builder)
    {
        var expanded = PathExpansion.Expand(SharedConfigPath);
        // optional: true → no crash on a fresh install where the file
        // hasn't been written yet. reloadOnChange: false → restart services
        // to pick up edits (we don't run inside file watchers, and a
        // mid-run config swap could change DB paths under live readers).
        var src = new JsonConfigurationSource
        {
            Path = expanded,
            Optional = true,
            ReloadOnChange = false,
        };
        src.ResolveFileProvider();

        var lastAppSettingsIdx = -1;
        for (var i = 0; i < builder.Sources.Count; i++)
        {
            if (builder.Sources[i] is JsonConfigurationSource json
                && json.Path is { } path
                && path.Contains("appsettings", StringComparison.OrdinalIgnoreCase))
            {
                lastAppSettingsIdx = i;
            }
        }
        if (lastAppSettingsIdx >= 0)
        {
            builder.Sources.Insert(lastAppSettingsIdx + 1, src);
        }
        else
        {
            builder.Sources.Add(src);
        }
        return builder;
    }
}
