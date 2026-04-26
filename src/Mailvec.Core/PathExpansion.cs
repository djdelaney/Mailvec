namespace Mailvec.Core;

public static class PathExpansion
{
    public static string Expand(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = path == "~" ? home : Path.Combine(home, path[2..]);
        }

        return Path.GetFullPath(path);
    }
}
