using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Eval;
using Mailvec.Core.Tests.Data;
using Mailvec.Core.Tests.Embedding;

namespace Mailvec.Core.Tests.Eval;

public sealed class EvalProvenanceTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private EvalReportProvenance Capture(ResolvedEmbeddingProfile profile) =>
        EvalProvenance.Capture(
            _db.Connections,
            new MetadataRepository(_db.Connections),
            new MessageRepository(_db.Connections),
            profile,
            _db.DatabasePath,
            mailvecVersion: "1.2.3");

    [Fact]
    public void Records_the_profile_identity_the_corpus_counts_and_the_stored_digest()
    {
        var metadata = new MetadataRepository(_db.Connections);
        metadata.Set(EmbeddingSpace.ModelDigestKey, "sha256:feed");

        var profile = TestProfiles.Legacy(queryPrefix: "Q: ");
        var p = Capture(profile);

        p.MailvecVersion.ShouldBe("1.2.3");
        p.MessageCount.ShouldBe(0);
        p.ChunkCount.ShouldBe(0);
        p.UnembeddedCount.ShouldBe(0);
        p.Embedding.ShouldNotBeNull();
        p.Embedding.SpaceId.ShouldBe("ollama:mxbai-embed-large:1024");
        p.Embedding.ConfigHash.ShouldBe(EmbeddingSpace.ForProfile(profile).ConfigHash);
        p.Embedding.Model.ShouldBe("mxbai-embed-large");
        p.Embedding.Dimensions.ShouldBe(1024);
        p.Embedding.ModelDigest.ShouldBe("sha256:feed");
        p.Embedding.QueryPrefix.ShouldBe("Q: ");
        p.Embedding.DocumentPrefix.ShouldBeNull();
        p.Embedding.EndpointIsLoopback.ShouldBeTrue();
    }

    [Fact]
    public void A_lan_endpoint_is_recorded_as_not_loopback_and_never_as_an_address()
    {
        var p = Capture(TestProfiles.Legacy() with { Endpoint = "http://192.168.1.50:11434" });

        p.Embedding!.EndpointIsLoopback.ShouldBeFalse();
        System.Text.Json.JsonSerializer.Serialize(p).ShouldNotContain("192.168");
    }

    [Fact]
    public void The_stored_identity_is_recorded_only_when_it_disagrees_with_the_profile()
    {
        var metadata = new MetadataRepository(_db.Connections);
        var profile = TestProfiles.Legacy();
        var (space, hash) = EmbeddingSpace.ForProfile(profile);

        metadata.Set(EmbeddingSpace.SpaceIdKey, space);
        metadata.Set(EmbeddingSpace.ConfigHashKey, hash);
        var agreeing = Capture(profile);
        agreeing.Embedding!.StoredSpaceId.ShouldBeNull();
        agreeing.Embedding.StoredConfigHash.ShouldBeNull();

        metadata.Set(EmbeddingSpace.SpaceIdKey, "ollama:qwen3-embedding:0.6b:1024");
        metadata.Set(EmbeddingSpace.ConfigHashKey, "other");
        var disagreeing = Capture(profile);
        disagreeing.Embedding!.StoredSpaceId.ShouldBe("ollama:qwen3-embedding:0.6b:1024");
        disagreeing.Embedding.StoredConfigHash.ShouldBe("other");
    }
}
