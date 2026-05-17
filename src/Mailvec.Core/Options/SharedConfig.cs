using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
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
    /// Insertion strategy: if an
    /// <see cref="EnvironmentVariablesConfigurationSource"/> is already in
    /// the chain (the default for <c>Host.CreateApplicationBuilder</c>),
    /// we insert immediately before it so env vars retain their natural
    /// precedence. Otherwise we append, letting the caller add env vars
    /// later (the pattern used by <c>CliServices</c>).
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

        var envIdx = -1;
        for (var i = 0; i < builder.Sources.Count; i++)
        {
            if (builder.Sources[i] is EnvironmentVariablesConfigurationSource)
            {
                envIdx = i;
                break;
            }
        }
        if (envIdx >= 0)
        {
            builder.Sources.Insert(envIdx, src);
        }
        else
        {
            builder.Sources.Add(src);
        }
        return builder;
    }
}
