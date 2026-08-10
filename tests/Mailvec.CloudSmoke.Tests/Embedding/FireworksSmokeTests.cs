using Mailvec.Core.Embedding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.CloudSmoke.Tests.Embedding;

/// <summary>
/// Calls the REAL hosted embedding API (Fireworks by default, or whatever
/// <c>Embedding:ActiveProfile</c>/<c>Embedding:Profiles:*</c> env vars name)
/// through the exact production registration path
/// (<see cref="EmbeddingRegistration.AddMailvecEmbedding"/>) that the
/// embedder, MCP and CLI all use. Unlike <c>OpenAiCompatibleTransportTests</c>
/// (hand-authored HTTP stubs, run on every PR), this is the check that the
/// provider's actual wire contract still matches what those stubs assume —
/// see docs/contributing/cloud-smoke-tests.md. Skips (does not fail) when no
/// credential is configured, so the normal test suite and a contributor's
/// local run are unaffected.
/// </summary>
public class FireworksSmokeTests
{
    private const string ApiKeyEnvVar = "Embedding__Profiles__fireworks-smoke__Auth__ApiKey";

    [Fact]
    public async Task Embeds_the_production_sentinel_texts_against_the_real_hosted_API()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyEnvVar)))
        {
            Console.WriteLine($"Skipping: {ApiKeyEnvVar} is not set. See docs/contributing/cloud-smoke-tests.md.");
            return;
        }

        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMailvecEmbedding(config, EmbeddingClientRole.Interactive);
        using var sp = services.BuildServiceProvider();

        var profile = sp.GetRequiredService<ResolvedEmbeddingProfile>();
        profile.Protocol.ShouldBe(EmbeddingRegistration.OpenAiCompatibleProtocol,
            $"Embedding:ActiveProfile must select an openai-compatible profile (got '{profile.Name}'/'{profile.Protocol}') " +
            "for this to actually exercise the hosted path.");

        var vectors = await sp.GetRequiredService<IEmbeddingService>()
            .EmbedDocumentsAsync(EmbeddingSpace.SentinelTexts);

        // The assertion is deliberately shape-only, not value-equality against
        // a stored reference: long-term revision drift is production's job
        // (the sentinel-drift mechanism in EmbeddingSpace, exercised live by
        // the embedder). This test's job is narrower — "the wire integration
        // still works" — auth accepted, request shape accepted, response
        // parsed, vectors sane.
        vectors.Length.ShouldBe(EmbeddingSpace.SentinelTexts.Count);
        foreach (var vector in vectors)
        {
            vector.Length.ShouldBe(profile.OutputDimensions);
            vector.ShouldAllBe(v => float.IsFinite(v));
            vector.Any(v => v != 0f).ShouldBeTrue("embedding vector must not be all-zero");
        }
    }
}
