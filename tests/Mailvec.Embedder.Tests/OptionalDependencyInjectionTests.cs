using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Embedder.Tests;

/// <summary>
/// Pins the DI behavior the embedder relies on: EmbeddingWorker takes the OCR
/// service as an optional (defaulted) constructor parameter, registered only on
/// platforms where the native renderer is supported. So we need: registered →
/// injected; not registered → falls back to the default (null, OCR skipped).
/// This isolates that contract so a framework change can't silently disable OCR.
/// </summary>
public class OptionalDependencyInjectionTests
{
    private sealed class Dependency;

    private sealed class Consumer(Dependency? dependency = null)
    {
        public Dependency? Dependency { get; } = dependency;
    }

    [Fact]
    public void A_registered_optional_ctor_param_is_injected()
    {
        var provider = new ServiceCollection()
            .AddSingleton<Dependency>()
            .AddSingleton<Consumer>()
            .BuildServiceProvider();

        provider.GetRequiredService<Consumer>().Dependency.ShouldNotBeNull();
    }

    [Fact]
    public void An_unregistered_optional_ctor_param_falls_back_to_its_default()
    {
        var provider = new ServiceCollection()
            .AddSingleton<Consumer>()
            .BuildServiceProvider();

        provider.GetRequiredService<Consumer>().Dependency.ShouldBeNull();
    }
}
