using Mailvec.Core.Eval;
using Mailvec.Core.Search;

namespace Mailvec.Core.Tests.Eval;

/// <summary>
/// Coverage for <see cref="DbEvalRankingSource"/>'s pure-logic concerns:
/// per-mode dispatch and the vector-leg k-inflation for filtered queries.
/// Can't easily exercise the actual searches (sealed services, real DB +
/// Ollama deps), but we can prove the unknown-mode path throws.
/// </summary>
public sealed class DbEvalRankingSourceTests
{
    [Fact]
    public async Task Unknown_EvalMode_throws_ArgumentOutOfRange()
    {
        // We can construct the source with null deps because the unknown-mode
        // path short-circuits before touching any of them. Belt-and-braces:
        // if a future contributor adds an EvalMode value but forgets to wire
        // a case, this guards against the default branch silently degrading.
        var src = new DbEvalRankingSource(keyword: null!, vector: null!, hybrid: null!);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await src.RankAsync("q", (EvalMode)999, topK: 10, filters: null, CancellationToken.None));
    }
}
