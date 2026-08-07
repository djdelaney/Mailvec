using Mailvec.Core.Mistral;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Mailvec.Core.Vision;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Tests.Vision;

/// <summary>
/// <see cref="IVisionClient.ModelId"/> is written verbatim into
/// <c>attachments.ocr_model</c> on every row a pass touches, and
/// <c>mailvec reocr --engine &lt;id&gt;</c> matches it with <c>=</c>. So the
/// shape is a stored-data contract, not a log string: change it and every row
/// written before the change becomes unselectable by the value written after.
/// </summary>
public class VisionModelIdTests
{
    [Fact]
    public void Ollama_reports_provider_colon_model()
    {
        var client = new OllamaVisionClient(
            new HttpClient(),
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions { VisionModel = "qwen2.5vl:7b" }),
            NullLogger<OllamaVisionClient>.Instance);

        client.ModelId.ShouldBe("ollama:qwen2.5vl:7b");
    }

    [Fact]
    public void Mistral_reports_provider_colon_model_and_excludes_the_endpoint()
    {
        // The endpoint is deployment state, not engine identity. Including it
        // would bake an internal hostname into thousands of rows and into
        // anything that ever surfaces the column, and it would make the id
        // change whenever the deployment moved — silently orphaning every row
        // written under the old URL from `--engine` matching.
        //
        // VisionRegistration.Describe DOES append it, deliberately, because
        // /health has one reader and no persistence. This asserts ModelId did
        // not quietly become Describe.
        var opts = new VisionOptions
        {
            Provider = VisionOptions.ProviderMistral,
            Mistral = new MistralVisionOptions
            {
                Model = "mistral-ocr-4-0",
                Endpoint = "https://hactarai.services.ai.azure.com",
                ApiKey = "not-a-real-key",
            },
        };
        var client = new MistralOcrClient(
            new HttpClient(), Microsoft.Extensions.Options.Options.Create(opts), NullLogger<MistralOcrClient>.Instance);

        client.ModelId.ShouldBe("mistral:mistral-ocr-4-0");
        client.ModelId.ShouldNotContain("azure.com");
        client.ModelId.ShouldNotContain("https");
    }

    [Fact]
    public void The_two_providers_never_collide()
    {
        // Same model name under different providers must remain distinguishable,
        // or "re-OCR everything the old engine produced" silently spans both.
        var ollama = new OllamaVisionClient(
            new HttpClient(), Microsoft.Extensions.Options.Options.Create(new OllamaOptions { VisionModel = "shared-name" }),
            NullLogger<OllamaVisionClient>.Instance);
        var mistral = new MistralOcrClient(
            new HttpClient(),
            Microsoft.Extensions.Options.Options.Create(new VisionOptions { Mistral = new MistralVisionOptions { Model = "shared-name" } }),
            NullLogger<MistralOcrClient>.Instance);

        ollama.ModelId.ShouldNotBe(mistral.ModelId);
    }

    [Fact]
    public void No_client_reports_the_pre_provider_sentinel_as_its_own_id()
    {
        // OcrProvenance.PreProvider means "no engine ran". A client that reported
        // it as its own identity would make real verdicts indistinguishable from
        // documents nothing ever looked at, and would let --overwrite protection
        // shield rows it was never meant to.
        var ollama = new OllamaVisionClient(
            new HttpClient(), Microsoft.Extensions.Options.Options.Create(new OllamaOptions()), NullLogger<OllamaVisionClient>.Instance);
        var mistral = new MistralOcrClient(
            new HttpClient(), Microsoft.Extensions.Options.Options.Create(new VisionOptions()), NullLogger<MistralOcrClient>.Instance);

        ollama.ModelId.ShouldNotBe(OcrProvenance.PreProvider);
        mistral.ModelId.ShouldNotBe(OcrProvenance.PreProvider);
    }
}
