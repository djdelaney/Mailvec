using Mailvec.Core;

namespace Mailvec.Core.Tests;

public sealed class PathExpansionTests
{
    private static readonly string Home =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void TildeAlone_ExpandsToHome()
    {
        Assert.Equal(Path.GetFullPath(Home), PathExpansion.Expand("~"));
    }

    [Fact]
    public void TildeSlash_ExpandsToHomeSubpath()
    {
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Home, "Mail/Fastmail")),
            PathExpansion.Expand("~/Mail/Fastmail"));
    }

    [Fact]
    public void DollarBraceHome_ExpandsToHome()
    {
        // Claude Desktop's MCPB user_config defaults pass ${HOME} verbatim
        // (host expands ${user_config.X} but not shell tokens). Without this,
        // ConnectionFactory tries to mkdir at /${HOME}/... and crashes.
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Home, "Library/Application Support/Mailvec/archive.sqlite")),
            PathExpansion.Expand("${HOME}/Library/Application Support/Mailvec/archive.sqlite"));
    }

    [Fact]
    public void DollarHome_ExpandsToHome()
    {
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Home, "Mail")),
            PathExpansion.Expand("$HOME/Mail"));
    }

    [Fact]
    public void DollarHomePrefix_OnlyMatchesAsWholeSegment()
    {
        // $HOMEWARD must NOT be treated as $HOME + WARD. The literal token
        // should survive into the resolved path (will be resolved against cwd
        // by Path.GetFullPath, but the segment name stays intact).
        var result = PathExpansion.Expand("$HOMEWARD/foo");
        Assert.Contains("$HOMEWARD", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsolutePath_PassedThrough()
    {
        Assert.Equal("/tmp/foo", PathExpansion.Expand("/tmp/foo"));
    }
}
