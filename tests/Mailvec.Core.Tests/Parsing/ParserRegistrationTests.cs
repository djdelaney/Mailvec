using Mailvec.Core.Parsing;
using Mailvec.Parsing.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Core.Tests.Parsing;

/// <summary>
/// ParserRegistration is the one place that decides which IMailParser a
/// process gets (the VisionRegistration rule): every host passes the same
/// shape of factory and gets the same resolution. These pin the resolution
/// rules themselves — the in-process factory is invoked with the configured
/// size gate, remote mode is refused until phase 2 lands rather than silently
/// falling back to in-process, and an unknown mode is fatal.
/// </summary>
public class ParserRegistrationTests
{
    private sealed class FakeParser(long attachmentMaxBytes) : IMailParser
    {
        public long AttachmentMaxBytes { get; } = attachmentMaxBytes;
        public string Mode => "fake";
        public ParsedMessage ParseMessage(byte[] eml, bool extractAttachmentText) => throw new NotSupportedException();
        public Core.Attachments.ExtractionResult ExtractAttachmentText(byte[] eml, int partIndex) => throw new NotSupportedException();
        public PartInfo DescribePart(byte[] eml, int partIndex) => throw new NotSupportedException();
        public DecodedPart DecodePart(byte[] eml, int partIndex, long? maxBytes) => throw new NotSupportedException();
        public PdfRender RenderPdfPages(byte[] eml, int partIndex, int firstPage, int maxPages, long? maxBytes) => throw new NotSupportedException();
        public Pdf.NormalizedImage? NormalizeImage(byte[] eml, int partIndex, long? maxBytes) => throw new NotSupportedException();
        public string? BodyTextFromHtml(string html, string? subject) => throw new NotSupportedException();
    }

    private static IServiceProvider Build(Dictionary<string, string?> settings, out int factoryCalls)
    {
        int calls = 0;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddMailvecParser(config, (_, s) => { calls++; return new FakeParser(s.AttachmentMaxBytes); });
        var sp = services.BuildServiceProvider();
        factoryCalls = calls;
        return sp;
    }

    [Fact]
    public void Default_mode_is_in_process_and_carries_the_indexer_size_gate()
    {
        var sp = Build(new() { ["Indexer:AttachmentMaxBytes"] = "12345" }, out _);

        var parser = sp.GetRequiredService<IMailParser>().ShouldBeOfType<FakeParser>();
        parser.AttachmentMaxBytes.ShouldBe(12345);
    }

    [Fact]
    public void Absent_size_gate_falls_back_to_the_IndexerOptions_default()
    {
        var sp = Build(new(), out _);

        sp.GetRequiredService<IMailParser>().ShouldBeOfType<FakeParser>()
            .AttachmentMaxBytes.ShouldBe(new Core.Options.IndexerOptions().AttachmentMaxBytes);
    }

    [Theory]
    [InlineData("inprocess")]
    [InlineData("InProcess")]
    [InlineData("  inprocess ")]
    public void Explicit_in_process_mode_is_case_and_whitespace_insensitive(string mode)
    {
        var sp = Build(new() { ["Parser:Mode"] = mode }, out _);
        sp.GetRequiredService<IMailParser>().ShouldBeOfType<FakeParser>();
    }

    [Fact]
    public void The_factory_is_not_invoked_until_the_parser_is_resolved()
    {
        // Registration must not force-load the in-process implementation: in
        // remote mode the lambda is never called, which is what lets the
        // container image strip the parser assemblies from the privileged
        // binaries' directories.
        Build(new(), out var callsAtRegistration);
        callsAtRegistration.ShouldBe(0);
    }

    [Fact]
    public void Remote_mode_resolves_the_remote_client_without_touching_the_in_process_factory()
    {
        var sp = Build(new() { ["Parser:Mode"] = "remote", ["Parser:Endpoint"] = "http://parse:3400" }, out _);

        sp.GetRequiredService<IMailParser>().ShouldBeOfType<RemoteParser>().Mode.ShouldBe("remote");
    }

    [Fact]
    public void Remote_mode_without_an_endpoint_is_fatal_rather_than_a_silent_fallback()
    {
        var sp = Build(new() { ["Parser:Mode"] = "remote" }, out _);

        var ex = Should.Throw<InvalidOperationException>(() => sp.GetRequiredService<IMailParser>());
        ex.Message.ShouldContain("Parser:Endpoint");
    }

    [Fact]
    public void Remote_mode_with_a_relative_endpoint_is_fatal()
    {
        var sp = Build(new() { ["Parser:Mode"] = "remote", ["Parser:Endpoint"] = "parse:3400" }, out _);

        Should.Throw<InvalidOperationException>(() => sp.GetRequiredService<IMailParser>())
            .Message.ShouldContain("absolute");
    }

    [Fact]
    public void An_unknown_mode_is_fatal()
    {
        var sp = Build(new() { ["Parser:Mode"] = "sidecar" }, out _);

        var ex = Should.Throw<InvalidOperationException>(() => sp.GetRequiredService<IMailParser>());
        ex.Message.ShouldContain("sidecar");
    }
}
