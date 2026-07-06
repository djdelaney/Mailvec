namespace Mailvec.Mcp;

/// <summary>
/// Builds the actionable "why is the archive empty" hint that rides along on
/// tool responses when there are zero messages. The failure this exists for:
/// the MCPB bundle *looks* standalone, but installed on a machine that never
/// ran <c>ops/install.sh</c> there is no shared config, the default DB path
/// resolves to nowhere, and <c>SchemaMigrator.EnsureUpToDate()</c> silently
/// creates a fresh empty database — every search returns zero results with no
/// error anywhere. The client LLM reads this hint and can tell the user what
/// to actually do, instead of concluding their mail contains nothing.
/// </summary>
internal static class SetupHints
{
    /// <summary>
    /// Null when the archive has messages (the overwhelmingly common case —
    /// callers pay only a long comparison). For an empty archive, the shared
    /// config file's presence picks between "the installer never ran" and
    /// "installed but the indexer hasn't produced anything".
    /// </summary>
    internal static string? EmptyArchiveHint(long totalMessages, bool sharedConfigExists, string dbPath)
    {
        if (totalMessages > 0) return null;

        return sharedConfigExists
            ? "The archive database is empty (0 messages). Mailvec is installed but the indexer has not " +
              $"ingested anything yet — or this server resolved an unexpected database path ({dbPath}). " +
              "Suggest the user run `mailvec status` to see counts and `mailvec doctor` for a full diagnosis; " +
              "if mbsync has never synced, their Maildir may still be empty."
            : "The archive database is empty and Mailvec's shared configuration file was not found — this " +
              "machine has likely never run Mailvec's installer. The MCPB bundle is only the read-side: " +
              "the user must run `ops/install.sh` from the Mailvec repository first (it writes the shared " +
              "config and installs the indexer/embedder services that populate the archive), then reconnect. " +
              "See docs/clients/claude-desktop.md in the repo.";
    }
}
