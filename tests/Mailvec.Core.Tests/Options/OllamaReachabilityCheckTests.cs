using Mailvec.Core.Options;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Mailvec.Core.Tests.Options;

public class OllamaReachabilityCheckTests
{
    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://[::1]:11434", true)]
    [InlineData("http://LOCALHOST:11434", true)]
    [InlineData("http://192.168.1.50:11434", false)]
    [InlineData("http://ollama:11434", false)]
    [InlineData("", false)]
    [InlineData("not a url", false)]
    public void Recognises_loopback_urls(string url, bool expected) =>
        OllamaReachabilityCheck.IsLoopback(url).ShouldBe(expected);

    [Fact]
    public void Warns_once_when_containerised_and_pointed_at_loopback()
    {
        // The configuration that has no working interpretation: inside a
        // container, loopback is the container itself. This is what an operator
        // gets by commenting OLLAMA_BASE_URL out of .env, since compose then
        // falls back to the localhost default rather than disabling Ollama.
        var log = new CapturingLogger();

        OllamaReachabilityCheck.WarnIfUnreachableFromContainer(
            "http://localhost:11434", log, inContainer: true);

        log.Warnings.Count.ShouldBe(1);
        // The message has to carry the fix, not just the symptom.
        log.Warnings[0].ShouldContain("OLLAMA_BASE_URL");
        // And it must pre-empt the wrong inference that a hosted OCR provider
        // removed the need for Ollama, which is what got us here.
        log.Warnings[0].ShouldContain("VISION model only");
    }

    [Fact]
    public void Silent_on_a_real_lan_address()
    {
        var log = new CapturingLogger();
        OllamaReachabilityCheck.WarnIfUnreachableFromContainer(
            "http://192.168.1.50:11434", log, inContainer: true);
        log.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Silent_outside_a_container_even_on_loopback()
    {
        // A bare-metal install running Ollama on the same host is the normal,
        // correct case. Warning there would put a permanent false alarm on
        // every local dev run — and an always-on warning is one nobody reads.
        var log = new CapturingLogger();
        OllamaReachabilityCheck.WarnIfUnreachableFromContainer(
            "http://localhost:11434", log, inContainer: false);
        log.Warnings.ShouldBeEmpty();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
