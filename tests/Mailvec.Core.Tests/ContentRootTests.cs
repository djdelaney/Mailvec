using System.Text.RegularExpressions;

namespace Mailvec.Core.Tests;

/// <summary>
/// Every host must name its content root. The .NET default is the working
/// directory, which made each service load whatever <c>appsettings.json</c>
/// sat where it was started — including one naming a native library to load.
/// The hosts are top-level programs, so this reads their source: a new host,
/// or an old one reverted to <c>CreateBuilder(args)</c>, fails here.
/// </summary>
public class ContentRootTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailvec.slnx"))) dir = dir.Parent;
        return dir.ShouldNotBeNull("Mailvec.slnx not found above the test directory").FullName;
    }

    [Fact]
    public void No_host_is_built_from_the_working_directory()
    {
        var sources = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var builders = new Regex(@"\b(CreateApplicationBuilder|CreateBuilder|CreateDefaultBuilder|CreateSlimBuilder|CreateEmptyBuilder)\s*\(");

        int found = 0;
        foreach (var file in sources)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) || !builders.IsMatch(lines[i])) continue;
                found++;
                // The settings object follows the call, on this line or the next.
                var call = lines[i] + " " + (i + 1 < lines.Length ? lines[i + 1] : "");
                call.ShouldContain("ContentRootPath = AppContext.BaseDirectory",
                    customMessage: $"{Path.GetRelativePath(RepoRoot(), file)}:{i + 1} builds a host without pinning its content root");
            }
        }

        // Mcp (stdio and HTTP), Indexer, Embedder, Parse. Zero would mean the
        // pattern stopped matching and this test stopped checking anything.
        found.ShouldBeGreaterThanOrEqualTo(5);
    }
}
