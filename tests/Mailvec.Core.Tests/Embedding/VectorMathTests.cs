using Mailvec.Core.Embedding;

namespace Mailvec.Core.Tests.Embedding;

public class VectorMathTests
{
    [Fact]
    public void A_vector_too_small_for_a_float_inverse_still_normalizes_to_finite_values()
    {
        // The norm's inverse (1e42) does not fit a float. Multiplying by the
        // resulting infinity gave [Infinity, NaN, NaN, ...], after the
        // caller's finiteness check, and that went into sqlite-vec.
        var vec = new float[8];
        vec[0] = 1e-42f;

        VectorMath.NormalizeInPlaceIfNeeded(vec);

        vec.ShouldAllBe(v => float.IsFinite(v));
        vec[0].ShouldBe(1f, 1e-3f);
        vec[1..].ShouldAllBe(v => v == 0f);
    }

    [Fact]
    public void An_ordinary_vector_normalizes_exactly_as_it_did_before()
    {
        // Stored vectors were produced by multiplying by the float inverse.
        // The small-norm path must not change what the common path returns.
        float[] vec = [3f, 4f, 12f, 0.5f];
        float[] expected = [.. vec];
        double sum = 0;
        foreach (var v in expected) sum += (double)v * v;
        var inv = (float)(1d / Math.Sqrt(sum));
        for (int i = 0; i < expected.Length; i++) expected[i] *= inv;

        VectorMath.NormalizeInPlaceIfNeeded(vec);

        vec.ShouldBe(expected);
    }
}
