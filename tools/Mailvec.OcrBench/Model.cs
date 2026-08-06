using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mailvec.OcrBench;

/// <summary>
/// On-disk shapes for the bake-off. A run is three files plus a materialised
/// working directory:
///
///   <workdir>/manifest.json        the sample (documents, pages, references)
///   <workdir>/docs/&lt;id&gt;.pdf       the exact PDF bytes each engine sees
///   <workdir>/pages/&lt;id&gt;-pN.jpg   the exact rendered page each page-mode engine sees
///   <workdir>/ref/&lt;id&gt;-pN.txt     reference text (truth set only)
///   <workdir>/results-&lt;engine&gt;.json
///
/// Materialising the bytes is the point: every engine is scored against
/// identical input, so a render-setting change can't masquerade as a quality
/// difference between engines. Re-sampling invalidates results — the manifest
/// carries the renderer settings so <c>score</c> can refuse a mismatch.
/// </summary>
internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
    }

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidOperationException($"{path} deserialised to null.");
}

/// <summary>Which corpus slice a sample was drawn from.</summary>
internal enum SampleSet
{
    /// <summary>
    /// PDFs the indexer extracted natively (extraction_status='done'), i.e. ones
    /// carrying a real embedded text layer. PdfPig's per-page text is the
    /// reference — objective, unlabelled, from the same corpus. Measures ceiling
    /// accuracy on clean typography, NOT robustness to real scan degradation.
    /// </summary>
    Truth,

    /// <summary>
    /// Genuine scanned PDFs (extraction_status='ocr'): image-only, no text layer,
    /// so no reference exists. Scored engine-vs-engine (agreement, length,
    /// latency) and by eyeball; this is the population the OCR pass actually
    /// serves.
    /// </summary>
    Scans,
}

internal sealed record RendererSettings(int Dpi, int MaxEdgePx, int JpegQuality);

internal sealed record PageSample(
    int Index,
    string ImagePath,
    string? ReferencePath,
    int ReferenceChars);

internal sealed record DocumentSample(
    long AttachmentId,
    long MessageId,
    string? FileName,
    long SizeBytes,
    string PdfPath,
    int PdfPageCount,
    IReadOnlyList<PageSample> Pages);

internal sealed record Manifest(
    string CreatedUtc,
    SampleSet Set,
    RendererSettings Renderer,
    IReadOnlyList<DocumentSample> Documents)
{
    public int TotalPages => Documents.Sum(d => d.Pages.Count);
}

internal sealed record PageResult(
    int Index,
    long ElapsedMs,
    string Text,
    string? Error = null);

internal sealed record DocumentResult(
    long AttachmentId,
    long ElapsedMs,
    IReadOnlyList<PageResult> Pages,
    string? Error = null);

/// <summary>
/// One engine's output over one sample. <paramref name="Mode"/> is "page" (one
/// call per rendered page image) or "document" (one call per whole PDF).
/// </summary>
internal sealed record RunResult(
    string Engine,
    string EngineDetail,
    string Mode,
    string StartedUtc,
    long WallClockMs,
    IReadOnlyList<DocumentResult> Documents);
