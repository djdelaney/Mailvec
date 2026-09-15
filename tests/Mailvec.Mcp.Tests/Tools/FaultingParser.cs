using Mailvec.Core.Attachments;
using Mailvec.Core.Parsing;
using Mailvec.Parsing;
using Mailvec.Parsing.Contracts;
using Mailvec.Pdf;

namespace Mailvec.Mcp.Tests.Tools;

/// <summary>
/// An <see cref="IMailParser"/> that delegates to a real one but lets a test
/// inject a failure into any operation by name — the way the remote parser
/// fails when the parse service is down or dies on a document. Phase 3 of
/// the parser isolation is the callers branching on that failure's
/// <see cref="ParseFailureKind"/>; these tests pin each caller's branch.
/// </summary>
public sealed class FaultingParser(IMailParser? inner = null) : IMailParser
{
    private readonly IMailParser _inner = inner ?? new InProcessParser(extractor: null);

    /// <summary>Called before every operation with its name; a non-null result is thrown.</summary>
    public Func<string, Exception?> Fault { get; set; } = _ => null;

    /// <summary>Operations attempted, faulted or not.</summary>
    public List<string> Calls { get; } = [];

    public static ParseException Unavailable() => new(ParseFailureKind.Unavailable, "The parse service is unreachable: connection refused");
    public static ParseException Crashed() => new(ParseFailureKind.Crashed, "The parse service timed out on this document and is restarting.");

    private T Run<T>(string op, Func<T> call)
    {
        Calls.Add(op);
        if (Fault(op) is { } ex) throw ex;
        return call();
    }

    public string Mode => _inner.Mode;
    public ParsedMessage ParseMessage(byte[] eml, bool extractAttachmentText) =>
        Run(nameof(ParseMessage), () => _inner.ParseMessage(eml, extractAttachmentText));
    public ExtractionResult ExtractAttachmentText(byte[] eml, int partIndex) =>
        Run(nameof(ExtractAttachmentText), () => _inner.ExtractAttachmentText(eml, partIndex));
    public PartInfo DescribePart(byte[] eml, int partIndex) =>
        Run(nameof(DescribePart), () => _inner.DescribePart(eml, partIndex));
    public DecodedPart DecodePart(byte[] eml, int partIndex, long? maxBytes) =>
        Run(nameof(DecodePart), () => _inner.DecodePart(eml, partIndex, maxBytes));
    public PdfRender RenderPdfPages(byte[] eml, int partIndex, int firstPage, int maxPages, long? maxBytes) =>
        Run(nameof(RenderPdfPages), () => _inner.RenderPdfPages(eml, partIndex, firstPage, maxPages, maxBytes));
    public NormalizedImage? NormalizeImage(byte[] eml, int partIndex, long? maxBytes) =>
        Run(nameof(NormalizeImage), () => _inner.NormalizeImage(eml, partIndex, maxBytes));
    public string? BodyTextFromHtml(string html, string? subject) =>
        Run(nameof(BodyTextFromHtml), () => _inner.BodyTextFromHtml(html, subject));
    public Task<bool> ProbeAsync(CancellationToken ct = default) => Task.FromResult(Fault("Probe") is null);
}
