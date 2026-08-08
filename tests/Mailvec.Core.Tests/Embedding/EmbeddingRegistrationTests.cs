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
    public void Profile_overrides_are_written_back_onto_OllamaOptions()
    {
        // The phase-2a bridge: consumers still reading Ollama:* directly
        // (worker verify, health, search prefix) must see the profile's
        // values, or the profile would split-brain inside one process.
        var sp = BuildProvider(Config(
            ("Ollama:EmbeddingModel", "mxbai-embed-large"),
            ("Ollama:EmbeddingDimensions", "1024"),
            ("Embedding:ActiveProfile", "qwen-local"),
            ("Embedding:Profiles:qwen-local:Protocol", "ollama"),
            ("Embedding:Profiles:qwen-local:Request:Model", "qwen3-embedding:4b"),
            ("Embedding:Profiles:qwen-local:OutputDimensions", "2560")),
            EmbeddingClientRole.Interactive);

        var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
        opts.EmbeddingModel.ShouldBe("qwen3-embedding:4b");
        opts.EmbeddingDimensions.ShouldBe(2560);
        EmbeddingSpace.FromOllamaOptions(opts).SpaceId.ShouldBe("ollama:qwen3-embedding:4b:2560");
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

    [Fact]
    public void OpenAi_compatible_protocol_is_recognised_but_not_yet_activatable()
    {
        var ex = Should.Throw<NotSupportedException>(() => EmbeddingRegistration.Resolve(Config(
            ("Embedding:ActiveProfile", "fw"),
            ("Embedding:Profiles:fw:Protocol", "openai-compatible"))));
        ex.Message.ShouldContain("phase 3");
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
            sp.GetRequiredService<IEmbeddingClient>().ShouldBeOfType<OllamaClient>();
            sp.GetRequiredService<ResolvedEmbeddingProfile>().Name.ShouldBe("ollama-legacy");
        }
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
