using Mailvec.Core.Eval;

namespace Mailvec.Core.Tests.Eval;

public sealed class EvalDefaultsTests
{
    private static readonly string Home =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void ResolveQuerySetPath_NullOverride_UsesDefaultUnderHome()
    {
        var resolved = EvalDefaults.ResolveQuerySetPath(null);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(Home, "Library/Application Support/Mailvec/eval/queries.json")),
            resolved);
    }

    [Fact]
    public void ResolveQuerySetPath_WhitespaceOverride_FallsBackToDefault()
    {
        var resolved = EvalDefaults.ResolveQuerySetPath("   ");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(Home, "Library/Application Support/Mailvec/eval/queries.json")),
            resolved);
    }

    [Fact]
    public void ResolveQuerySetPath_TildeOverride_ExpandsHome()
    {
        var resolved = EvalDefaults.ResolveQuerySetPath("~/custom/queries.json");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(Home, "custom/queries.json")),
            resolved);
    }

    [Fact]
    public void ResolveQuerySetPath_AbsoluteOverride_PassedThrough()
    {
        var resolved = EvalDefaults.ResolveQuerySetPath("/tmp/queries.json");

        Assert.Equal("/tmp/queries.json", resolved);
    }
}
