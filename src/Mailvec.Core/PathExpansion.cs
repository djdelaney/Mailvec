namespace Mailvec.Core;

public static class PathExpansion
{
    public static string Expand(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // ~ form (canonical Unix shorthand).
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
        {
            path = path == "~" ? home : Path.Combine(home, path[2..]);
        }
        // ${HOME} / $HOME form. Some hosts (Claude Desktop's MCPB user_config
        // defaults among them, observed Apr 2026) substitute their own template
        // tokens like ${user_config.X} but leave shell-style ${HOME} verbatim,
        // so we expand it ourselves rather than rely on the launcher.
        else if (path.StartsWith("${HOME}", StringComparison.Ordinal))
        {
            path = home + path["${HOME}".Length..];
        }
        else if (path.StartsWith("$HOME", StringComparison.Ordinal) &&
                 (path.Length == "$HOME".Length || path["$HOME".Length] == '/'))
        {
            path = home + path["$HOME".Length..];
        }

        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Inverse of <see cref="Expand"/>, for paths that get persisted or
    /// displayed: rewrites an absolute path under the user's home directory
    /// to the ~/ shorthand. Exists so artifacts that may be committed to the
    /// repo (eval baseline reports record the query-set path) never embed
    /// the local username. Expand round-trips the result.
    /// </summary>
    public static string Collapse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var full = Path.GetFullPath(path);

        if (full == home) return "~";
        var prefix = home.EndsWith('/') ? home : home + "/";
        return full.StartsWith(prefix, StringComparison.Ordinal)
            ? "~/" + full[prefix.Length..]
            : full;
    }
}
