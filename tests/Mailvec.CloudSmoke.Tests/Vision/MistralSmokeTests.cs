using Mailvec.Core.Options;
using Mailvec.Core.Vision;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mailvec.CloudSmoke.Tests.Vision;

/// <summary>
/// Calls the REAL hosted OCR API (mistral-ocr, via <c>Vision:Mistral:*</c> env
/// vars — api.mistral.ai or an Azure AI Foundry deployment) through the exact
/// production registration path (<see cref="VisionRegistration.AddMailvecVision"/>)
/// the embedder uses for its OCR pass. Unlike <c>MistralOcrClientTests</c>
/// (hand-authored HTTP stubs, run on every PR), this is the check that the
/// provider's actual response shape still matches what those stubs assume —
/// see docs/contributing/cloud-smoke-tests.md. The image is entirely
/// fabricated (tests/Mailvec.CloudSmoke.Tests/Assets/ocr-smoke-sample.png) —
/// no real mail content. Skips (does not fail) when no credential is
/// configured, so the normal test suite and a contributor's local run are
/// unaffected.
/// </summary>
public class MistralSmokeTests
{
    private const string ApiKeyEnvVar = "Vision__Mistral__ApiKey";
    private const string SentinelToken = "SENTINEL-4F2A9C";

    [Fact]
    public async Task Transcribes_the_fabricated_sample_against_the_real_hosted_API()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyEnvVar)))
        {
            Console.WriteLine($"Skipping: {ApiKeyEnvVar} is not set. See docs/contributing/cloud-smoke-tests.md.");
            return;
        }

        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMailvecVision(config, requiresCredentials: true);
        using var sp = services.BuildServiceProvider();

        var vision = sp.GetRequiredService<IOptions<VisionOptions>>().Value;
        vision.IsMistral.ShouldBeTrue(
            $"Vision:Provider must be 'mistral' (got '{vision.Provider}') for this to actually exercise the hosted path.");

        var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ocr-smoke-sample.png");
        var image = await File.ReadAllBytesAsync(assetPath);

        var text = await sp.GetRequiredService<IVisionClient>().OcrAsync(image);

        text.ShouldContain(SentinelToken);
    }
}
