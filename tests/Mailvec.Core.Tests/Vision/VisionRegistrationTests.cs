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

    // ---- Probe-only processes that DO hold credentials ----------------------

    [Fact]
    public async Task A_probe_only_process_refuses_a_cleartext_endpoint_instead_of_sending_the_key()
    {
        // It used to skip validation whenever the config was complete, so the
        // key went over http:// on every /health poll while the embedder
        // refused the same setting. Degrades, never throws: MCP must not
        // crashloop over an OCR setting.
        var client = Resolve(new()
        {
            ["Vision:Provider"] = "mistral",
            ["Vision:Mistral:Endpoint"] = "http://ocr.example.com",
            ["Vision:Mistral:Model"] = "mistral-ocr-4-0",
            ["Vision:Mistral:ApiKey"] = "k",
        });

        var probe = await client.ProbeAsync();
        probe.Status.ShouldBe(VisionProbeStatus.Misconfigured);
        probe.Detail.ShouldNotBeNull().ShouldContain("https");
    }

    [Fact]
    public void The_embedder_still_refuses_to_start_on_the_same_setting()
    {
        Should.Throw<InvalidOperationException>(() => Resolve(new()
        {
            ["Vision:Provider"] = "mistral",
            ["Vision:Mistral:Endpoint"] = "http://ocr.example.com",
            ["Vision:Mistral:Model"] = "mistral-ocr-4-0",
            ["Vision:Mistral:ApiKey"] = "k",
        }, requiresCredentials: true));
    }

    // ---- ApiKeyFile ------------------------------------------------------------

    [Fact]
    public void The_key_can_come_from_an_owner_only_file_and_is_trimmed()
    {
        if (OperatingSystem.IsWindows()) return;
        var keyFile = Path.Combine(Path.GetTempPath(), "mailvec-ocr-key-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(keyFile, "ocr_key_from_file\n");
        File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            var opts = new MistralVisionOptions { ApiKeyFile = keyFile };
            opts.ResolveApiKey().ShouldBe("ocr_key_from_file");
            new MistralVisionOptions { ApiKey = "  inline_wins \n", ApiKeyFile = keyFile }.ResolveApiKey().ShouldBe("inline_wins");

            File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            Should.Throw<InvalidOperationException>(() => opts.ResolveApiKey()).Message.ShouldContain("chmod 600");
        }
        finally { File.Delete(keyFile); }
    }

    [Fact]
    public void A_probe_only_process_without_the_key_file_mounted_is_not_configured_here()
    {
        // Compose mounts the OCR key into the embedder only; mcp/cli see the
        // option but not the file, which must read as the deliberate posture.
        var client = Resolve(new()
        {
            ["Vision:Provider"] = "mistral",
            ["Vision:Mistral:Endpoint"] = "https://ocr.example.com",
            ["Vision:Mistral:Model"] = "mistral-ocr-4-0",
            ["Vision:Mistral:ApiKeyFile"] = "/run/secrets/not-mounted-here",
        });
        client.ShouldBeOfType<UnconfiguredVisionClient>();
    }

    [Fact]
    public void An_inline_key_from_a_json_config_file_is_refused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mailvec-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var json = Path.Combine(dir, "appsettings.Local.json");
        File.WriteAllText(json, """{"Vision":{"Provider":"mistral","Mistral":{"Endpoint":"https://ocr.example.com","Model":"m","ApiKey":"leaked"}}}""");
        try
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(json).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.Configure<OllamaOptions>(configuration.GetSection(OllamaOptions.SectionName));

            Should.Throw<InvalidOperationException>(() => services.AddMailvecVision(configuration, requiresCredentials: true))
                .Message.ShouldContain("appsettings.Local.json");
        }
        finally { Directory.Delete(dir, recursive: true); }
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
