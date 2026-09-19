using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Health;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Parsing.Contracts;
using Mailvec.Pdf;
using Mailvec.Core.Tests.Data;

namespace Mailvec.Core.Tests.Health;

/// <summary>
/// The parser section of /health: which parser this process uses and, for
/// the remote one, whether the parse service answered. Informational like
/// Services and Mail — a parse service that is down must never turn the mcp
/// container's own healthcheck red.
/// </summary>
public class HealthServiceParserTests
{
    private static HealthService Build(TempDatabase db, IMailParser? parser, ParserOptions? options = null) =>
        new(db.Connections,
            new MetadataRepository(db.Connections),
            new EmbeddingService(new FakeEmbedding(), Tests.Embedding.TestProfiles.Legacy()),
            Tests.Embedding.TestProfiles.Legacy(),
            Microsoft.Extensions.Options.Options.Create(new ArchiveOptions { DatabasePath = db.DatabasePath }),
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions()),
            parser: parser,
            parserOptions: options is null ? null : Microsoft.Extensions.Options.Options.Create(options));

    [Fact]
    public async Task Without_a_parser_wired_the_section_is_absent()
    {
        using var db = new TempDatabase();

        var report = await Build(db, parser: null).CheckAsync();

        report.Parser.ShouldBeNull();
    }

    [Fact]
    public async Task An_in_process_parser_reports_its_mode_and_no_endpoint()
    {
        using var db = new TempDatabase();

        var report = await Build(db, new FakeParser("inprocess", reachable: true),
            new ParserOptions { Mode = "inprocess", Endpoint = "http://should-not-be-reported:1" }).CheckAsync();

        var parser = report.Parser.ShouldNotBeNull();
        parser.Mode.ShouldBe("inprocess");
        parser.Endpoint.ShouldBeNull();
        parser.Reachable.ShouldBe(true);
    }

    [Fact]
    public async Task A_remote_parser_that_is_down_is_reported_but_never_degrades_status()
    {
        using var db = new TempDatabase();

        var report = await Build(db, new FakeParser(ParserRegistration.RemoteMode, reachable: false),
            new ParserOptions { Mode = "remote", Endpoint = "http://parse:3400" }).CheckAsync();

        var parser = report.Parser.ShouldNotBeNull();
        parser.Mode.ShouldBe("remote");
        parser.Endpoint.ShouldBe("http://parse:3400");
        parser.Reachable.ShouldBe(false);
        report.Status.ShouldNotBe("degraded",
            "a parse service outage is the parse container's outage; restarting mcp for it would be wrong");
    }

    private sealed class FakeParser(string mode, bool reachable) : IMailParser
    {
        public string Mode => mode;
        public Task<bool> ProbeAsync(CancellationToken ct = default) => Task.FromResult(reachable);
        public ParsedMessage ParseMessage(byte[] eml, bool extractAttachmentText) => throw new NotSupportedException();
        public ExtractionResult ExtractAttachmentText(byte[] eml, int partIndex) => throw new NotSupportedException();
        public PartInfo DescribePart(byte[] eml, int partIndex) => throw new NotSupportedException();
        public DecodedPart DecodePart(byte[] eml, int partIndex, long? maxBytes) => throw new NotSupportedException();
        public PdfRender RenderPdfPages(byte[] eml, int partIndex, int firstPage, int maxPages, long? maxBytes) => throw new NotSupportedException();
        public NormalizedImage? NormalizeImage(byte[] eml, int partIndex, long? maxBytes) => throw new NotSupportedException();
        public string? BodyTextFromHtml(string html, string? subject) => throw new NotSupportedException();
    }

    private sealed class FakeEmbedding : IEmbeddingTransport
    {
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
        { var v = new float[1024]; v[0] = 1f; return Task.FromResult(new[] { v }); }
        public Task<bool?> IsModelAvailableAsync(CancellationToken ct = default) => Task.FromResult<bool?>(true);
    }
}
