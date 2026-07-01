using Mailvec.Core.Ollama;

namespace Mailvec.Core.Tests.Ollama;

/// <summary>
/// <see cref="OllamaVisionClient.CollapseRepeatedLines"/> squashes the
/// repetition-loop output a vision model emits on noisy images, without
/// harming legitimate text.
/// </summary>
public class CollapseRepeatedLinesTests
{
    [Fact]
    public void Collapses_a_long_run_of_one_line_to_a_single_occurrence()
    {
        var loop = "CAVITYBUSTERS.COM\n" + string.Concat(Enumerable.Repeat("Colonial\n", 500));
        var result = OllamaVisionClient.CollapseRepeatedLines(loop);

        result.ShouldBe("CAVITYBUSTERS.COM\nColonial");
    }

    [Fact]
    public void Only_collapses_consecutive_repeats_not_distinct_lines()
    {
        // Non-consecutive repeats (A B A) are preserved — only back-to-back runs go.
        OllamaVisionClient.CollapseRepeatedLines("A\nB\nA").ShouldBe("A\nB\nA");
        OllamaVisionClient.CollapseRepeatedLines("A\nA\nB\nB\nB\nC").ShouldBe("A\nB\nC");
    }

    [Fact]
    public void Leaves_ordinary_text_and_empties_untouched()
    {
        OllamaVisionClient.CollapseRepeatedLines("").ShouldBe("");
        const string doc = "Invoice #123\nTotal: $45.00\n\nThank you";
        OllamaVisionClient.CollapseRepeatedLines(doc).ShouldBe(doc);
    }
}
