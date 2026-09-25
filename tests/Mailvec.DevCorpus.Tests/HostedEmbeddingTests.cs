using System.Diagnostics;
using Mailvec.Core.Embedding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.DevCorpus.Tests;

/// <summary>
/// The --embedding profile as a shell actually receives it: env.sh is sourced
/// by a real bash and the resulting environment goes through the same
/// registration the embedder, MCP server and CLI use. No network call — this
/// proves the profile is valid and keyless, not that the provider answers.
/// </summary>
public sealed class HostedEmbeddingTests : IDisposable
{
    private readonly string _temp = TempDir.Create("mailvec-devcorpus-e-");

    public void Dispose() => TempDir.Delete(_temp);

    private string Generate(string? embedding) =>
        CorpusWriter.Write(Path.Combine(_temp, embedding ?? "none"), new CorpusOptions(Filler: 0, Embedding: embedding), TempDir.NoSharedConfig);

    /// <summary>Sources env.sh in bash and returns every Embedding__* / Embedder__* variable it exported.</summary>
    private static Dictionary<string, string> Source(string envSh)
    {
        var psi = new ProcessStartInfo("bash")
        {
            ArgumentList =
            {
                "-c",
                """. "$1" && for v in $(compgen -e | grep -E '^Embedd(ing|er)__'); do printf '%s=%s\0' "$v" "${!v}"; done""",
                "bash", envSh,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.ShouldBe(0, stderr);
        // Sourcing does not stop on a failed export (e.g. an invalid variable
        // name), so an error line is the only sign one was dropped.
        stderr.ShouldBeEmpty();
        return stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(kv => kv.Split('=', 2))
            .ToDictionary(kv => kv[0], kv => kv[1]);
    }

    private static IConfiguration Config(Dictionary<string, string> env) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(env.Select(kv => new KeyValuePair<string, string?>(kv.Key.Replace("__", ":"), kv.Value)))
            .Build();

    [Fact]
    public void The_fireworks_profile_resolves_through_the_real_registration_without_a_key()
    {
        var root = Generate(HostedEmbedding.Fireworks);
        var envSh = Path.Combine(root, "env.sh");
        var config = Config(Source(envSh));

        var profile = EmbeddingRegistration.Resolve(config);
        profile.Name.ShouldBe("fireworks_dev");
        profile.Protocol.ShouldBe("openai-compatible");
        profile.Endpoint.ShouldBe("https://api.fireworks.ai/inference/v1/embeddings");
        profile.OutputDimensions.ShouldBe(1024);
        profile.SpaceId.ShouldBe("fireworks:qwen3-embedding-8b:1024:devcorpus");
        // The newline survived the shell: printf built it, not a literal "\n".
        profile.QueryPrefix.ShouldEndWith("the query\nQuery: ");

        // The same registration every process runs; Scheme=none means it
        // must not demand key material. Building resolves the transport.
        var services = new ServiceCollection().AddLogging();
        services.AddMailvecEmbedding(config, EmbeddingClientRole.Interactive);
        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IEmbeddingService>().ShouldNotBeNull();

        config["Embedder:OcrEnabled"].ShouldBe("false");
        // Keyless AND routed through the environment's proxy — the only
        // combination registration permits for Proxy=environment.
        config["Embedding:Profiles:fireworks_dev:Proxy"].ShouldBe("environment");
    }

    [Fact]
    public void No_key_material_is_ever_written()
    {
        var env = File.ReadAllText(Path.Combine(Generate(HostedEmbedding.Fireworks), "env.sh"));
        env.ShouldNotContain("ApiKey");
        env.ShouldContain("Auth__Scheme='none'");
    }

    [Fact]
    public void Without_the_option_env_sh_configures_no_embedding()
    {
        Source(Path.Combine(Generate(null), "env.sh")).ShouldBeEmpty();
    }

    [Fact]
    public void An_unknown_profile_is_rejected_before_anything_is_written()
    {
        var target = Path.Combine(_temp, "bad");
        Should.Throw<ArgumentException>(() =>
            CorpusWriter.Write(target, new CorpusOptions(Embedding: "openai"), TempDir.NoSharedConfig));
        Directory.Exists(target).ShouldBeFalse();
    }
}
