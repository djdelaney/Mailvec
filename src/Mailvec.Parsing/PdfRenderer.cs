using System.Runtime.Versioning;
using PDFtoImage;
using SkiaSharp;

namespace Mailvec.Pdf;

/// <summary>
/// Rasterises a single PDF page to JPEG via PDFtoImage (PDFium + SkiaSharp,
/// native). Lives in its own project (not Core) so the native dep reaches only
/// Mailvec.Mcp (get_attachment_page_image) and Mailvec.Embedder (scanned-PDF
/// OCR) — not Core/Indexer/Cli. Self-contained enough to unit-test directly,
/// which doubles as the "does the native lib load on this RID" check.
///
/// PDFium is not thread-safe; PDFtoImage serialises all calls into it
/// internally, so callers don't add their own lock.
///
/// Sizing: render at 150 DPI (legible for tables on a letter/A4 page) but cap
/// the long edge at <see cref="MaxEdgePx"/>. Without the cap, a scan saved with
/// a large MediaBox renders to a multi-thousand-pixel, tens-of-MB image — a
/// pointless payload, since Claude downsamples every image to ~1568px long edge
/// before the model sees it. Capping is downscale-only: a normal page renders
/// at its natural 150-DPI size; only oversized pages are scaled down.
///
/// Output is JPEG, not PNG: page renders are photographic-ish (especially
/// scans), where lossless PNG is many times larger for no model-visible gain.
/// JPEG has no alpha, so we composite onto a white background (the correct
/// page colour) before encoding.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public static class PdfRenderer
{
    private const int Dpi = 150;
    private const int JpegQuality = 85;

    /// <summary>
    /// Long-edge ceiling in pixels, just under Claude's ~1568px image cap.
    /// Rendering larger wastes payload Claude would only throw away.
    /// </summary>
    public const int MaxEdgePx = Mailvec.Parsing.Contracts.RasterLimits.MaxEdgePx;

    /// <summary>Number of pages in the PDF. Throws if the bytes aren't a PDF PDFium can open.</summary>
    public static int PageCount(byte[] pdf) => Conversion.GetPageCount(pdf);

    /// <summary>Render a single 0-based page index to JPEG bytes, long edge capped at <see cref="MaxEdgePx"/>.</summary>
    public static byte[] RenderPageJpeg(byte[] pdf, int pageIndex) =>
        RenderPageJpeg(pdf, pageIndex, PageSizes(pdf)[pageIndex]);

    /// <summary>Every page's size in PDF user units (points). Read once per document, not once per page.</summary>
    public static IList<System.Drawing.SizeF> PageSizes(byte[] pdf) => Conversion.GetPageSizes(pdf);

    /// <summary>Render one page whose size the caller already read with <see cref="PageSizes"/>.</summary>
    public static byte[] RenderPageJpeg(byte[] pdf, int pageIndex, System.Drawing.SizeF sizePt)
    {
        var (w, h) = TargetPixels(sizePt.Width, sizePt.Height);

        using var bitmap = Conversion.ToImage(
            pdf, page: pageIndex,
            options: new RenderOptions(Width: w, Height: h, WithAspectRatio: true, BackgroundColor: SKColors.White));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return data.ToArray();
    }

    /// <summary>
    /// Pixel size to render a page of the given size (points) at, long edge
    /// capped at <see cref="MaxEdgePx"/>. The page size comes from the
    /// document's /MediaBox, so it is whatever the sender wrote: the scaling is
    /// done in floating point and each edge clamped to [1, MaxEdgePx] before
    /// it becomes an int, and a size that is not a positive finite number is
    /// refused rather than rounded to a 0-pixel render.
    /// </summary>
    internal static (int Width, int Height) TargetPixels(double widthPt, double heightPt)
    {
        if (!double.IsFinite(widthPt) || !double.IsFinite(heightPt) || widthPt <= 0 || heightPt <= 0)
            throw new InvalidDataException("The PDF page has no usable size.");

        double w = widthPt / 72.0 * Dpi;
        double h = heightPt / 72.0 * Dpi;
        double longEdge = Math.Max(w, h);
        if (longEdge > MaxEdgePx)
        {
            double scale = MaxEdgePx / longEdge;
            w *= scale;
            h *= scale;
        }
        return ((int)Math.Clamp(Math.Round(w), 1, MaxEdgePx), (int)Math.Clamp(Math.Round(h), 1, MaxEdgePx));
    }
}
