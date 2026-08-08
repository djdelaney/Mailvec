using Mailvec.Core.Embedding;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Tests.Embedding;

/// <summary>
/// Phase-2 registration/config contract (docs/proposals/embedding-providers.md):
/// one resolution, identical in every executable; absent config preserves the
/// legacy Ollama behavior byte-for-byte; unknown protocols are fatal, never a
/// fallback; and profile overrides cannot split-brain against consumers that
/// still read Ollama:* directly.
/// </summary>
public class EmbeddingRegistrationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    // ---------- legacy resolution (no Embedding section) ----------

    [Fact]
    public void Absent_section_resolves_the_legacy_Ollama_profile()
    {
        var resolved = EmbeddingRegistration.Resolve(Config(
            ("Ollama:EmbeddingModel", "mxbai-embed-large"),
            ("Ollama:EmbeddingDimensions", "1024"),
            ("Ollama:QueryInstructionPrefix", "Q: "),
            ("Ollama:MaxBatchSize", "16"),
            ("Ollama:RequestTimeoutSeconds", "60")));

        resolved.Name.ShouldBe("ollama-legacy");
        resolved.Protocol.ShouldBe("ollama");
        resolved.WireModel.ShouldBe("mxbai-embed-large");
        resolved.OutputDimensions.ShouldBe(1024);
        resolved.SpaceId.ShouldBe("ollama:mxbai-embed-large:1024");
        resolved.QueryPrefix.ShouldBe("Q: ");
        resolved.DocumentPrefix.ShouldBe("");
        resolved.MaxBatchSize.ShouldBe(16);
        resolved.RequestTimeoutSeconds.ShouldBe(60);
    }

    [Fact]
    public void The_same_configuration_resolves_identically_for_every_role()
    {
        // The split-brain guard: Embedder (BackgroundIngestion) and MCP/CLI
        // (Interactive) may differ in HTTP posture but never in identity.
        var config = Config(
            ("Ollama:EmbeddingModel", "mxbai-embed-large"),
            ("Ollama:EmbeddingDimensions", "1024"));

        ResolvedProfileFrom(config, EmbeddingClientRole.Interactive)
            .ShouldBe(ResolvedProfileFrom(config, EmbeddingClientRole.BackgroundIngestion));
    }

    // ---------- explicit profiles ----------

    [Fact]
    public void An_ollama_profile_overrides_the_vector_affecting_values()
    {
        var resolved = EmbeddingRegistration.Resolve(Config(
            ("Ollama:EmbeddingModel", "mxbai-embed-large"),
            ("Ollama:EmbeddingDimensions", "1024"),
            ("Embedding:ActiveProfile", "qwen-local"),
            ("Embedding:Profiles:qwen-local:Protocol", "ollama"),
            ("Embedding:Profiles:qwen-local:Request:Model", "qwen3-embedding:4b"),
            ("Embedding:Profiles:qwen-local:OutputDimensions", "2560"),
            ("Embedding:Profiles:qwen-local:Text:QueryPrefix", "Instruct: retrieve\nQuery: ")));

        resolved.Name.ShouldBe("qwen-local");
        resolved.WireModel.ShouldBe("qwen3-embedding:4b");
        resolved.OutputDimensions.ShouldBe(2560);
        resolved.SpaceId.ShouldBe("ollama:qwen3-embedding:4b:2560");
        resolved.QueryPrefix.ShouldBe("Instruct: retrieve\nQuery: ");
    }

    [Fact]
    public void The_resolved_profile_is_the_single_identity_source_and_options_stay_untouched()
    {
        // The phase-2a PostConfigure bridge is retired: consumers read the
        // registered ResolvedEmbeddingProfile, and OllamaOptions keeps its
        // own (legacy) values — nothing rewrites it behind the operator's
        // back. A consumer still deriving identity from options would
        // disagree with the profile here, which is what this pins.
        var sp = BuildProvider(Config(
            ("Ollama:EmbeddingModel", "mxbai-embed-large"),
            ("Ollama:EmbeddingDimensions", "1024"),
            ("Embedding:ActiveProfile", "qwen-local"),
            ("Embedding:Profiles:qwen-local:Protocol", "ollama"),
            ("Embedding:Profiles:qwen-local:Request:Model", "qwen3-embedding:4b"),
            ("Embedding:Profiles:qwen-local:OutputDimensions", "2560")),
            EmbeddingClientRole.Interactive);

        var profile = sp.GetRequiredService<ResolvedEmbeddingProfile>();
        profile.WireModel.ShouldBe("qwen3-embedding:4b");
        profile.OutputDimensions.ShouldBe(2560);
        EmbeddingSpace.ForProfile(profile).SpaceId.ShouldBe("ollama:qwen3-embedding:4b:2560");

        var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
        opts.EmbeddingModel.ShouldBe("mxbai-embed-large");
        opts.EmbeddingDimensions.ShouldBe(1024);
    }

    [Fact]
    public void An_explicitly_empty_query_prefix_is_a_value_not_an_inherit()
    {
        var resolved = EmbeddingRegistration.Resolve(Config(
            ("Ollama:QueryInstructionPrefix", "legacy-prefix: "),
            ("Embedding:ActiveProfile", "p"),
            ("Embedding:Profiles:p:Protocol", "ollama"),
            ("Embedding:Profiles:p:Text:QueryPrefix", "")));

        resolved.QueryPrefix.ShouldBe("");
    }

    // ---------- fatal misconfiguration ----------

    [Fact]
    public void Unknown_protocol_is_fatal_and_names_the_known_ones()
    {
        var ex = Should.Throw<InvalidOperationException>(() => EmbeddingRegistration.Resolve(Config(
            ("Embedding:ActiveProfile", "p"),
            ("Embedding:Profiles:p:Protocol", "grpc-custom"))));
        ex.Message.ShouldContain("grpc-custom");
        ex.Message.ShouldContain("never falls back");
    }

    // ---------- hosted (openai-compatible) profiles ----------

    private static (string, string?)[] FireworksConfig(params (string, string?)[] overrides)
    {
        var baseline = new (string, string?)[]
        {
            ("Embedding:ActiveProfile", "fw"),
            ("Embedding:Profiles:fw:Protocol", "openai-compatible"),
            ("Embedding:Profiles:fw:ProviderId", "fireworks"),
            ("Embedding:Profiles:fw:Endpoint", "https://api.fireworks.ai/inference/v1/embeddings"),
            ("Embedding:Profiles:fw:Request:Model", "accounts/fireworks/models/qwen3-embedding-8b"),
            ("Embedding:Profiles:fw:Request:EncodingFormat", "float"),
            ("Embedding:Profiles:fw:OutputDimensions", "1024"),
            ("Embedding:Profiles:fw:SpaceId", "fireworks:qwen3-embedding-8b:1024:adopted-2026-08"),
            ("Embedding:Profiles:fw:Auth:Scheme", "bearer"),
            ("Embedding:Profiles:fw:Auth:ApiKey", "fw_test_key"),
        };
        var merged = baseline.ToDictionary(p => p.Item1, p => p.Item2);
        foreach (var (key, value) in overrides) merged[key] = value;   // override wins
        return merged.Select(kv => (kv.Key, kv.Value)).ToArray();
    }

    [Fact]
    public void A_fireworks_profile_resolves_with_its_capability_policy()
    {
        var resolved = EmbeddingRegistration.Resolve(Config(FireworksConfig()));

        resolved.Protocol.ShouldBe("openai-compatible");
        resolved.ProviderId.ShouldBe("fireworks");
        resolved.WireModel.ShouldBe("accounts/fireworks/models/qwen3-embedding-8b");
        resolved.OutputDimensions.ShouldBe(1024);
        resolved.SpaceId.ShouldBe("fireworks:qwen3-embedding-8b:1024:adopted-2026-08");
        resolved.SendWireModel.ShouldBeTrue();
        resolved.SendDimensions.ShouldBeTrue();
        resolved.EncodingFormat.ShouldBe("float");
        // Hosted transforms default EMPTY — never inherited from the
        // Ollama-specific legacy prefix.
        resolved.QueryPrefix.ShouldBe("");
    }

    [Fact]
    public void A_baseten_style_placeholder_profile_omits_dimensions_and_keeps_the_placeholder_model()
    {
        var resolved = EmbeddingRegistration.Resolve(Config(FireworksConfig(
            ("Embedding:Profiles:fw:Request:Model", "not-required"),
            ("Embedding:Profiles:fw:Request:ModelParameter", "placeholder"),
            ("Embedding:Profiles:fw:Request:DimensionsParameter", "omit"))));

        resolved.WireModel.ShouldBe("not-required");
        resolved.SendWireModel.ShouldBeTrue();
        resolved.SendDimensions.ShouldBeFalse();
        resolved.OutputDimensions.ShouldBe(1024); // still validated against returns
    }

    [Fact]
    public void Omit_model_policy_still_requires_a_local_model_identity()
    {
        // Review phases 4-7, finding 5: the value doubles as the database's
        // model identity (fresh schema, switch-model) even when kept off the
        // wire — without it the profile resolved but could never create or
        // migrate a database.
        Should.Throw<InvalidOperationException>(() => EmbeddingRegistration.Resolve(Config(FireworksConfig(
            ("Embedding:Profiles:fw:Request:ModelParameter", "omit"),
            ("Embedding:Profiles:fw:Request:Model", null)))))
            .Message.ShouldContain("local model");

        var resolved = EmbeddingRegistration.Resolve(Config(FireworksConfig(
            ("Embedding:Profiles:fw:Request:ModelParameter", "omit"))));
        resolved.SendWireModel.ShouldBeFalse();
        resolved.WireModel.ShouldBe("accounts/fireworks/models/qwen3-embedding-8b");
    }

    [Fact]
    public void The_hosted_transport_is_registered_for_an_active_hosted_profile()
    {
        var sp = BuildProvider(Config(FireworksConfig()), EmbeddingClientRole.Interactive);
        sp.GetRequiredService<IEmbeddingTransport>().ShouldBeOfType<OpenAiCompatibleTransport>();
    }

    [Fact]
    public void Hosted_profiles_fail_startup_on_each_missing_precondition()
    {
        // SpaceId (decision 3), HTTPS, endpoint, model-when-sent, dimensions,
        // encoding, auth scheme — every one is fatal, never a fallback.
        foreach (var (broken, mustMention) in new (string, string)[]
        {
            ("Embedding:Profiles:fw:SpaceId", "SpaceId"),
            ("Embedding:Profiles:fw:Endpoint", "Endpoint"),
            ("Embedding:Profiles:fw:Request:Model", "Request:Model"),
            ("Embedding:Profiles:fw:OutputDimensions", "OutputDimensions"),
        })
        {
            var config = Config(FireworksConfig().Where(p => p.Item1 != broken).ToArray());
            Should.Throw<InvalidOperationException>(() => EmbeddingRegistration.Resolve(config))
                .Message.ShouldContain(mustMention, customMessage: broken);
        }

        Should.Throw<InvalidOperationException>(() => EmbeddingRegistration.Resolve(Config(FireworksConfig(
            ("Embedding:Profiles:fw:Endpoint", "http://api.fireworks.ai/v1/embeddings")))))
            .Message.ShouldContain("HTTPS");
        Should.Throw<InvalidOperationException>(() => EmbeddingRegistration.Resolve(Config(FireworksConfig(
            ("Embedding:Profiles:fw:Request:EncodingFormat", "base64")))))
            .Message.ShouldContain("base64");
        Should.Throw<InvalidOperationException>(() => EmbeddingRegistration.Resolve(Config(FireworksConfig(
            ("Embedding:Profiles:fw:Auth:Scheme", "custom-header")))))
            .Message.ShouldContain("bearer");
    }

    [Fact]
    public void A_loopback_http_endpoint_is_allowed_for_stub_tests()
    {
        var resolved = EmbeddingRegistration.Resolve(Config(FireworksConfig(
            ("Embedding:Profiles:fw:Endpoint", "http://127.0.0.1:9999/v1/embeddings"))));
        resolved.Endpoint.ShouldBe("http://127.0.0.1:9999/v1/embeddings");
    }

    [Fact]
    public void Bearer_scheme_without_key_material_is_fatal_and_a_key_file_is_read()
    {
        Should.Throw<InvalidOperationException>(() =>
            EmbeddingRegistration.ResolveBearerToken(Config(FireworksConfig(
                ("Embedding:Profiles:fw:Auth:ApiKey", null))), "fw"))
            .Message.ShouldContain("bearer");

        var keyFile = Path.Combine(Path.GetTempPath(), "mailvec-test-key-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(keyFile, "fw_from_file\n");
        try
        {
            EmbeddingRegistration.ResolveBearerToken(Config(FireworksConfig(
                ("Embedding:Profiles:fw:Auth:ApiKey", null),
                ("Embedding:Profiles:fw:Auth:ApiKeyFile", keyFile))), "fw")
                .ShouldBe("fw_from_file");
        }
        finally { File.Delete(keyFile); }
    }

    [Fact]
    public void Missing_active_profile_is_fatal_and_lists_what_exists()
    {
        var ex = Should.Throw<InvalidOperationException>(() => EmbeddingRegistration.Resolve(Config(
            ("Embedding:ActiveProfile", "nope"),
            ("Embedding:Profiles:real:Protocol", "ollama"))));
        ex.Message.ShouldContain("nope");
        ex.Message.ShouldContain("real");
    }

    [Fact]
    public void An_ollama_profile_may_not_assert_a_space_id()
    {
        // Decision 3's inverse face: hosted profiles MUST assert, Ollama
        // profiles MUST NOT — theirs is derived and digest-enforced.
        var ex = Should.Throw<InvalidOperationException>(() => EmbeddingRegistration.Resolve(Config(
            ("Embedding:ActiveProfile", "p"),
            ("Embedding:Profiles:p:Protocol", "ollama"),
            ("Embedding:Profiles:p:SpaceId", "ollama:mxbai-embed-large:1024"))));
        ex.Message.ShouldContain("SpaceId");
    }

    [Fact]
    public void An_ollama_profile_may_not_set_an_endpoint()
    {
        var ex = Should.Throw<InvalidOperationException>(() => EmbeddingRegistration.Resolve(Config(
            ("Embedding:ActiveProfile", "p"),
            ("Embedding:Profiles:p:Protocol", "ollama"),
            ("Embedding:Profiles:p:Endpoint", "http://elsewhere:11434"))));
        ex.Message.ShouldContain("Ollama:BaseUrl");
    }

    [Fact]
    public void All_four_text_transforms_resolve_and_each_changes_the_config_hash()
    {
        // Every transform is vector-affecting (the proposal defines all
        // four); each must be carried by the resolved profile AND covered by
        // the canonical hash, or a configured transform could silently
        // split the space.
        var resolved = EmbeddingRegistration.Resolve(Config(
            ("Embedding:ActiveProfile", "p"),
            ("Embedding:Profiles:p:Protocol", "ollama"),
            ("Embedding:Profiles:p:Text:QueryPrefix", "q<"),
            ("Embedding:Profiles:p:Text:QuerySuffix", ">q"),
            ("Embedding:Profiles:p:Text:DocumentPrefix", "d<"),
            ("Embedding:Profiles:p:Text:DocumentSuffix", ">d")));

        resolved.QueryPrefix.ShouldBe("q<");
        resolved.QuerySuffix.ShouldBe(">q");
        resolved.DocumentPrefix.ShouldBe("d<");
        resolved.DocumentSuffix.ShouldBe(">d");

        var baseline = EmbeddingSpace.ForProfile(resolved with
        { QueryPrefix = "", QuerySuffix = "", DocumentPrefix = "", DocumentSuffix = "" }).ConfigHash;
        EmbeddingSpace.ForProfile(resolved with { QuerySuffix = "", DocumentPrefix = "", DocumentSuffix = "" })
            .ConfigHash.ShouldNotBe(baseline);
        EmbeddingSpace.ForProfile(resolved with { QueryPrefix = "", DocumentPrefix = "", DocumentSuffix = "" })
            .ConfigHash.ShouldNotBe(baseline);
        EmbeddingSpace.ForProfile(resolved with { QueryPrefix = "", QuerySuffix = "", DocumentSuffix = "" })
            .ConfigHash.ShouldNotBe(baseline);
        EmbeddingSpace.ForProfile(resolved with { QueryPrefix = "", QuerySuffix = "", DocumentPrefix = "" })
            .ConfigHash.ShouldNotBe(baseline);
    }

    // ---------- registration wiring ----------

    [Fact]
    public void The_registered_client_is_the_ollama_client_for_both_roles()
    {
        foreach (var role in new[] { EmbeddingClientRole.Interactive, EmbeddingClientRole.BackgroundIngestion })
        {
            var sp = BuildProvider(Config(
                ("Ollama:EmbeddingModel", "mxbai-embed-large"),
                ("Ollama:EmbeddingDimensions", "1024")), role);
            sp.GetRequiredService<IEmbeddingTransport>().ShouldBeOfType<OllamaClient>();
            sp.GetRequiredService<ResolvedEmbeddingProfile>().Name.ShouldBe("ollama-legacy");
        }
    }

    [Fact]
    public async Task Interactive_hosted_requests_retry_backpressure_but_never_auth_failures()
    {
        // Review P2: interactive hosted requests had NO retry pipeline —
        // 429/503 are routine serverless conditions and failed straight
        // through to the user. Auth errors must still fail fast. Exercised
        // end-to-end through the registration-built pipeline against a real
        // loopback server.
        var port = new Random().Next(20000, 60000);
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var requests = 0;
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { break; }
                var n = Interlocked.Increment(ref requests);
                if (n == 1)
                {
                    ctx.Response.StatusCode = 429;
                    ctx.Response.Headers.Add("Retry-After", "0");
                    ctx.Response.Close();
                }
                else
                {
                    var vec = string.Join(",", Enumerable.Repeat("0.5", 4));
                    var body = System.Text.Encoding.UTF8.GetBytes(
                        $"{{\"data\":[{{\"index\":0,\"embedding\":[{vec}]}}]}}");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
            }
        });

        var sp = BuildProvider(Config(FireworksConfig(
            ("Embedding:Profiles:fw:Endpoint", $"http://127.0.0.1:{port}/v1/embeddings"),
            ("Embedding:Profiles:fw:OutputDimensions", "4"),
            ("Embedding:Profiles:fw:Auth:Scheme", "none"),
            ("Embedding:Profiles:fw:Auth:ApiKey", null))), EmbeddingClientRole.Interactive);

        var vector = await sp.GetRequiredService<IEmbeddingService>().EmbedQueryAsync("hello");
        vector.Length.ShouldBe(4);
        requests.ShouldBe(2);   // the 429 was retried once, honoring Retry-After

        listener.Stop();
        await Task.WhenAny(serving, Task.Delay(1000));
    }

    private static ResolvedEmbeddingProfile ResolvedProfileFrom(IConfiguration config, EmbeddingClientRole role) =>
        BuildProvider(config, role).GetRequiredService<ResolvedEmbeddingProfile>();

    private static ServiceProvider BuildProvider(IConfiguration config, EmbeddingClientRole role)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<OllamaOptions>(config.GetSection(OllamaOptions.SectionName));
        services.AddMailvecEmbedding(config, role);
        return services.BuildServiceProvider();
    }
}
