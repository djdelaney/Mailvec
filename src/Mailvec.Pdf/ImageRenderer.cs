using System.Runtime.Versioning;
using SkiaSharp;

namespace Mailvec.Pdf;

/// <summary>
/// Decodes and normalises an image *attachment* for vision OCR: composites onto
/// a white background (flattening any alpha), downscales the long edge to
/// <see cref="PdfRenderer.MaxEdgePx"/>, and re-encodes JPEG q85 — the same
/// payload shape <see cref="PdfRenderer.RenderPageJpeg"/> produces, so the
/// vision model sees one consistent input regardless of source. Reports the
/// *decoded* pixel dimensions so the OCR pass can gate out icons / banners /
/// spacers that slipped past the cheap byte pre-filter.
///
/// SkiaSharp (native) decodes JPEG/PNG/GIF/BMP/WEBP out of the box; formats it
/// can't decode on a given platform (e.g. HEIC without the system codec) return
/// null — a graceful skip, not a crash. Native, so it lives here next to
/// PdfRenderer rather than in Core.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public static class ImageRenderer
{
    private const int JpegQuality = 85;

    private static readonly SKSamplingOptions Sampling =
        new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>
    /// Decode + normalise <paramref name="bytes"/> into an OCR-ready JPEG.
    /// Returns null when the bytes aren't a decodable image — the caller marks
    /// the attachment terminally so it isn't re-selected every cycle. The
    /// reported <see cref="NormalizedImage.Width"/>/<see cref="NormalizedImage.Height"/>
    /// are the *source* dimensions (pre-downscale): that's what the dimension /
    /// aspect-ratio gate keys off.
    /// </summary>
    public static NormalizedImage? TryNormalize(byte[] bytes)
    {
        // NB: SKBitmap.Decode does NOT reliably return null on undecodable input.
        // When SkiaSharp's native build has no codec for the bytes — TIFF and
        // HEIC in particular, both common as email attachments — SKCodec.Create
        // returns null and SKBitmap.Decode(byte[]) then throws
        // ArgumentNullException("codec"). Treat any decode/encode failure as
        // "not an OCR-able raster" and return null, so the caller marks the
        // attachment failed instead of the whole OCR batch aborting and
        // retrying the poison bytes forever.
        try
        {
            using var src = SKBitmap.Decode(bytes);
            if (src is null || src.Width == 0 || src.Height == 0) return null;

            int srcW = src.Width, srcH = src.Height;

            int w = srcW, h = srcH;
            int longEdge = Math.Max(w, h);
            if (longEdge > PdfRenderer.MaxEdgePx)
            {
                double scale = (double)PdfRenderer.MaxEdgePx / longEdge;
                w = Math.Max(1, (int)Math.Round(w * scale));
                h = Math.Max(1, (int)Math.Round(h * scale));
            }

            // Opaque surface + white clear flattens alpha to the correct backdrop
            // before the (alpha-less) JPEG encode.
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            using (var srcImage = SKImage.FromBitmap(src))
            {
                canvas.DrawImage(srcImage, new SKRect(0, 0, w, h), Sampling);
            }
            canvas.Flush();

            using var outImage = surface.Snapshot();
            using var data = outImage.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            return new NormalizedImage(srcW, srcH, data.ToArray());
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Decoded source dimensions + the normalised OCR-ready JPEG bytes.</summary>
public sealed record NormalizedImage(int Width, int Height, byte[] Jpeg);
