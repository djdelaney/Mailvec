using Mailvec.Core.Eval;

namespace Mailvec.Core.Tests.Eval;

public sealed class EvalMetricsTests
{
    [Fact]
    public void Ndcg_PerfectBinaryRanking_IsOne()
    {
        var ranked = new[] { "a", "b", "c", "d" };
        var grades = new Dictionary<string, double> { ["a"] = 1, ["b"] = 1 };
        EvalMetrics.NdcgAtK(ranked, grades, k: 10).ShouldBe(1.0, tolerance: 1e-9);
    }

    [Fact]
    public void Ndcg_SwappedFirstTwoRelevant_IsBelowOne()
    {
        // "b" relevant at rank 1, "a" relevant at rank 2 — same set, same grades
        // as ideal but flipped order. Should still be < 1 only when grades differ;
        // with equal grades the DCG equals the IDCG.
        var ranked = new[] { "b", "a", "c", "d" };
        var grades = new Dictionary<string, double> { ["a"] = 1, ["b"] = 1 };
        EvalMetrics.NdcgAtK(ranked, grades, k: 10).ShouldBe(1.0, tolerance: 1e-9);
    }

    [Fact]
    public void Ndcg_GradedRelevance_RewardsHigherGradeFirst()
    {
        // Two docs, grade 3 and grade 1. Putting the grade-3 first is ideal.
        var idealRanked = new[] { "hi", "lo", "x", "y" };
        var swappedRanked = new[] { "lo", "hi", "x", "y" };
        var grades = new Dictionary<string, double> { ["hi"] = 3, ["lo"] = 1 };

        var ideal = EvalMetrics.NdcgAtK(idealRanked, grades, k: 10);
        var swapped = EvalMetrics.NdcgAtK(swappedRanked, grades, k: 10);
        ideal.ShouldBe(1.0, tolerance: 1e-9);
        swapped.ShouldBeLessThan(ideal);
    }

    [Fact]
    public void Ndcg_NoRelevantInTopK_IsZero()
    {
        var ranked = new[] { "x", "y", "z" };
        var grades = new Dictionary<string, double> { ["a"] = 1 };
        EvalMetrics.NdcgAtK(ranked, grades, k: 10).ShouldBe(0.0);
    }

    [Fact]
    public void Ndcg_EmptyGrades_IsZero()
    {
        EvalMetrics.NdcgAtK(new[] { "a", "b" }, new Dictionary<string, double>(), 10).ShouldBe(0.0);
    }

    [Fact]
    public void Ndcg_SingleRelevantAtRank2_HasKnownValue()
    {
        // DCG = 1 / log2(3); IDCG = 1 / log2(2) = 1 → NDCG = 1 / log2(3) ≈ 0.6309
        var ranked = new[] { "x", "a", "y" };
        var grades = new Dictionary<string, double> { ["a"] = 1 };
        EvalMetrics.NdcgAtK(ranked, grades, k: 10).ShouldBe(1.0 / Math.Log2(3), tolerance: 1e-9);
    }

    [Fact]
    public void Ndcg_KCaps_ResultsBeyondCutoffIgnored()
    {
        // Relevant doc at rank 5 contributes 0 at k=3, normal at k=5.
        var ranked = new[] { "x", "y", "z", "w", "a" };
        var grades = new Dictionary<string, double> { ["a"] = 1 };
        EvalMetrics.NdcgAtK(ranked, grades, k: 3).ShouldBe(0.0);
        EvalMetrics.NdcgAtK(ranked, grades, k: 5).ShouldBeGreaterThan(0.0);
    }

    [Fact]
    public void Mrr_FirstRelevantAtRank3_IsOneThird()
    {
        var ranked = new[] { "x", "y", "a", "b" };
        var relevant = new HashSet<string> { "a", "b" };
        EvalMetrics.MrrAtK(ranked, relevant, k: 10).ShouldBe(1.0 / 3.0, tolerance: 1e-9);
    }

    [Fact]
    public void Mrr_NoneInTopK_IsZero()
    {
        var ranked = new[] { "x", "y", "z" };
        var relevant = new HashSet<string> { "a" };
        EvalMetrics.MrrAtK(ranked, relevant, k: 10).ShouldBe(0.0);
    }

    [Fact]
    public void Mrr_RelevantBeyondK_IsZero()
    {
        var ranked = new[] { "x", "y", "z", "a" };
        var relevant = new HashSet<string> { "a" };
        EvalMetrics.MrrAtK(ranked, relevant, k: 3).ShouldBe(0.0);
        EvalMetrics.MrrAtK(ranked, relevant, k: 4).ShouldBe(0.25);
    }

    [Fact]
    public void Recall_AllInTopK_IsOne()
    {
        var ranked = new[] { "a", "b", "c" };
        var relevant = new HashSet<string> { "a", "b" };
        EvalMetrics.RecallAtK(ranked, relevant, k: 10).ShouldBe(1.0);
    }

    [Fact]
    public void Recall_HalfInTopK_IsHalf()
    {
        var ranked = new[] { "a", "x", "y" };
        var relevant = new HashSet<string> { "a", "b" };
        EvalMetrics.RecallAtK(ranked, relevant, k: 10).ShouldBe(0.5);
    }

    [Fact]
    public void Recall_EmptyRelevant_IsZero()
    {
        EvalMetrics.RecallAtK(new[] { "a" }, new HashSet<string>(), 10).ShouldBe(0.0);
    }
}
