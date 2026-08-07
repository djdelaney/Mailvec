using Mailvec.Core.Mistral;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Mailvec.Core.Vision;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Mailvec.Core.Tests.Vision;

public class VisionRegistrationTests
{
    [Fact]
    public void Defaults_to_ollama_so_an_existing_install_needs_no_config_change()
    {
        Resolve([]).ShouldBeOfType<OllamaVisionClient>();
    }

    [Fact]
    public void Selects_the_hosted_client_when_configured()
    {
        var client = Resolve(new()
        {
            ["Vision:Provider"] = "mistral",
            ["Vision:Mistral:Endpoint"] = "https://example.invalid",
            ["Vision:Mistral:Model"] = "deployment-1",
            ["Vision:Mistral:ApiKey"] = "k",
        }, requiresCredentials: true);

        client.ShouldBeOfType<MistralOcrClient>();
    }

    [Fact]
    public async Task A_probe_only_process_without_credentials_degrades_instead_of_throwing()
    {
        // The compose posture: the API key is scoped to the embedder, so the MCP
        // server and CLI see Vision:Provider=mistral with nothing to call it
        // with. Throwing here would crashloop the MCP container — taking down
        // search and every tool call — because an OCR key was missing. Wildly
        // disproportionate, so it reports unavailable instead.
        var client = Resolve(new() { ["Vision:Provider"] = "mistral" }, requiresCredentials: false);

        client.ShouldBeOfType<UnconfiguredVisionClient>();
        (await client.IsModelAvailableAsync()).ShouldBeFalse();
    }

    [Fact]
    public void The_process_that_actually_OCRs_refuses_to_start_without_credentials()
    {
        // The opposite call for the embedder: starting cleanly and then quietly
        // never OCRing anything is the worst available outcome, so incomplete
        // config is fatal where the work happens.
        var ex = Should.Throw<InvalidOperationException>(() =>
            Resolve(new() { ["Vision:Provider"] = "mistral" }, requiresCredentials: true));

        ex.Message.ShouldContain("Vision:Mistral:Endpoint");
    }

    [Fact]
    public void The_api_key_error_points_away_from_the_world_readable_shared_config()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            Resolve(new()
            {
                ["Vision:Provider"] = "mistral",
                ["Vision:Mistral:Endpoint"] = "https://example.invalid",
                ["Vision:Mistral:Model"] = "d",
            }, requiresCredentials: true));

        ex.Message.ShouldContain("environment variable");
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("openai")]
    public void An_unknown_provider_name_is_fatal_in_every_process(string provider)
    {
        // Falling back to a local model that isn't running is indistinguishable
        // from "OCR is quietly doing nothing" — the one failure mode worth
        // crashing over.
        foreach (var requiresCredentials in new[] { true, false })
        {
            var ex = Should.Throw<InvalidOperationException>(() =>
                Resolve(new() { ["Vision:Provider"] = provider }, requiresCredentials));
            ex.Message.ShouldContain("Vision:Provider must be");
        }
    }

    [Fact]
    public void Provider_name_is_case_insensitive()
    {
        Resolve(new() { ["Vision:Provider"] = "Ollama" }).ShouldBeOfType<OllamaVisionClient>();
    }

    // ---- Endpoint transport security ------------------------------------------
    //
    // The API key travels on every request, alongside base64 rendered pages of
    // the user's mail, and the OCR pass submits them UNATTENDED — nobody is
    // present to notice a cleartext endpoint. The documents most likely to
    // reach it are the scanned ones: statements, tax forms, medical letters.

    [Theory]
    [InlineData("http://mistral.example.com")]
    [InlineData("http://10.0.0.5:8080")]
    public void A_cleartext_endpoint_is_refused(string endpoint)
    {
        var ex = Should.Throw<InvalidOperationException>(() => Resolve(new()
        {
            ["Vision:Provider"] = "mistral",
            ["Vision:Mistral:Endpoint"] = endpoint,
            ["Vision:Mistral:Model"] = "d",
            ["Vision:Mistral:ApiKey"] = "k",
        }, requiresCredentials: true));

        ex.Message.ShouldContain("https");
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    public void A_non_absolute_endpoint_is_refused(string endpoint)
    {
        var ex = Should.Throw<InvalidOperationException>(() => Resolve(new()
        {
            ["Vision:Provider"] = "mistral",
            ["Vision:Mistral:Endpoint"] = endpoint,
            ["Vision:Mistral:Model"] = "d",
            ["Vision:Mistral:ApiKey"] = "k",
        }, requiresCredentials: true));

        // "not-a-url" fails the absolute parse; "/relative/path" parses on Unix
        // as file:///relative/path (absolute, and IsLoopback true), so it is the
        // scheme check that has to catch it.
        ex.Message.ShouldContain("absolute", Case.Insensitive);
    }

    [Theory]
    [InlineData("https://mistral.example.com")]
    [InlineData("http://localhost:8080")]   // loopback exception, for local mocks
    [InlineData("http://127.0.0.1:8080")]
    public void Https_and_loopback_are_accepted(string endpoint)
    {
        Resolve(new()
        {
            ["Vision:Provider"] = "mistral",
            ["Vision:Mistral:Endpoint"] = endpoint,
            ["Vision:Mistral:Model"] = "d",
            ["Vision:Mistral:ApiKey"] = "k",
        }, requiresCredentials: true).ShouldBeOfType<MistralOcrClient>();
    }

    private static IVisionClient Resolve(
        Dictionary<string, string?> settings, bool requiresCredentials = false)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<OllamaOptions>(configuration.GetSection(OllamaOptions.SectionName));
        services.AddMailvecVision(configuration, requiresCredentials);
        return services.BuildServiceProvider().GetRequiredService<IVisionClient>();
    }
}
