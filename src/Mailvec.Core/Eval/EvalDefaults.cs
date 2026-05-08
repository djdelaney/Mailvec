namespace Mailvec.Core.Eval;

public static class EvalDefaults
{
    public const string DefaultQuerySetPath = "~/Library/Application Support/Mailvec/eval/queries.json";

    public static string ResolveQuerySetPath(string? overridePath = null) =>
        PathExpansion.Expand(string.IsNullOrWhiteSpace(overridePath) ? DefaultQuerySetPath : overridePath);
}
