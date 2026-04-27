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
}
