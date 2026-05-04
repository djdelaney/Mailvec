using Mailvec.Core.Data;

namespace Mailvec.Core.Tests.Data;

public class VectorBlobTests
{
    [Fact]
    public void Roundtrips_a_typical_embedding_vector()
    {
        var original = new float[] { 0.1f, -0.2f, 3.14159f, 0f, float.MaxValue, float.MinValue };

        var bytes = VectorBlob.Serialize(original);
        var restored = VectorBlob.Deserialize(bytes);

        restored.ShouldBe(original);
    }

    [Fact]
    public void Serialize_emits_four_bytes_per_float()
    {
        var bytes = VectorBlob.Serialize(new float[] { 1f, 2f, 3f, 4f });

        bytes.Length.ShouldBe(16);
    }

    [Fact]
    public void Serialize_uses_little_endian_layout_required_by_sqlite_vec()
    {
        // sqlite-vec expects packed little-endian float32. Pin the byte layout
        // so a future endianness regression fails loudly here, not as silently
        // garbage similarity scores in production.
        var bytes = VectorBlob.Serialize(new float[] { 1.0f });

        bytes.ShouldBe(new byte[] { 0x00, 0x00, 0x80, 0x3F });
    }

    [Fact]
    public void Empty_vector_roundtrips_to_empty_bytes()
    {
        var bytes = VectorBlob.Serialize([]);

        bytes.ShouldBeEmpty();
        VectorBlob.Deserialize(bytes).ShouldBeEmpty();
    }

    [Fact]
    public void Deserialize_throws_on_non_multiple_of_four_length()
    {
        var bad = new byte[] { 0x01, 0x02, 0x03 };

        var ex = Should.Throw<ArgumentException>(() => VectorBlob.Deserialize(bad));
        ex.Message.ShouldContain("3");
    }

    [Fact]
    public void Roundtrips_a_full_1024_dimension_vector()
    {
        // The schema pins chunk_embeddings to FLOAT[1024]; make sure the
        // production-shaped vector survives serialize/deserialize intact.
        var original = new float[1024];
        for (int i = 0; i < original.Length; i++) original[i] = i * 0.001f;

        var restored = VectorBlob.Deserialize(VectorBlob.Serialize(original));

        restored.Length.ShouldBe(1024);
        restored.ShouldBe(original);
    }
}
