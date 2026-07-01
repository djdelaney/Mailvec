using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BitMiracle.LibTiff.Classic;
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
/// SkiaSharp (native) decodes JPEG/PNG/GIF/BMP/WEBP out of the box. TIFF — which
/// SkiaSharp's native build has no codec for, yet is common as scanned-document
/// attachments — is decoded via the pure-managed BitMiracle.LibTiff.NET and
/// handed to the same normalise path. HEIC is still unsupported (would need
/// native libheif) and returns null — a graceful skip, not a crash.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public static class ImageRenderer
{
    private const int JpegQuality = 85;

    // Guard against a small compressed TIFF that decompresses to a gigantic
    // raster (width*height*4 bytes). 40 MP ≈ 160 MB — well above any real scan.
    private const long MaxTiffPixels = 40_000_000;

    private static readonly SKSamplingOptions Sampling =
        new(SKFilterMode.Linear, SKMipmapMode.Linear);

    static ImageRenderer()
    {
        // LibTiff logs warnings/errors to stderr by default (many benign TIFFs
        // trip warnings); silence it so it doesn't pollute the embedder log.
        Tiff.SetErrorHandler(new SilentTiffErrorHandler());
    }

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
        // When SkiaSharp's native build has no codec for the bytes (HEIC, corrupt
        // JPEG), SKCodec.Create returns null and SKBitmap.Decode(byte[]) then
        // throws ArgumentNullException("codec"). Treat any decode/encode failure
        // as "not an OCR-able raster" and return null, so the caller marks the
        // attachment failed instead of the whole OCR batch aborting and retrying
        // the poison bytes forever.
        try
        {
            using var src = Decode(bytes);
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

    /// <summary>Decode to an <see cref="SKBitmap"/>, routing TIFF to LibTiff since SkiaSharp can't.</summary>
    private static SKBitmap? Decode(byte[] bytes)
    {
        if (IsTiff(bytes)) return DecodeTiff(bytes);
        // JPEG/PNG/GIF/BMP/WEBP; throws on HEIC/corrupt → caught by TryNormalize.
        return SKBitmap.Decode(bytes);
    }

    /// <summary>TIFF magic: "II" + 42/43 (little-endian) or "MM" + 42/43 (big-endian). 43 = BigTIFF.</summary>
    private static bool IsTiff(byte[] b)
    {
        if (b.Length < 4) return false;
        bool le = b[0] == 0x49 && b[1] == 0x49; // II
        bool be = b[0] == 0x4D && b[1] == 0x4D; // MM
        if (!le && !be) return false;
        int magic = le ? (b[2] | (b[3] << 8)) : ((b[2] << 8) | b[3]);
        return magic == 42 || magic == 43;
    }

    private static SKBitmap? DecodeTiff(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var tiff = Tiff.ClientOpen("mem", "r", ms, new TiffStream());
        if (tiff is null) return null;

        int width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        int height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        if (width <= 0 || height <= 0 || (long)width * height > MaxTiffPixels) return null;

        // ReadRGBAImageOriented gives a top-left-origin ABGR-packed raster,
        // decoding any TIFF flavour (tiled/striped, 1/8/16-bit, palette, CMYK…)
        // to RGBA — exactly what we want to hand SkiaSharp.
        var raster = new int[width * height];
        if (!tiff.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT, stopOnError: false))
            return null;

        // Repack ABGR ints → RGBA bytes (via the LibTiff channel accessors, so
        // this is endian-independent) and copy into a Skia-owned bitmap.
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < raster.Length; i++)
        {
            int p = raster[i];
            int o = i * 4;
            pixels[o]     = (byte)Tiff.GetR(p);
            pixels[o + 1] = (byte)Tiff.GetG(p);
            pixels[o + 2] = (byte)Tiff.GetB(p);
            pixels[o + 3] = (byte)Tiff.GetA(p);
        }

        var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
        return bmp;
    }

    private sealed class SilentTiffErrorHandler : TiffErrorHandler
    {
        public override void WarningHandler(Tiff tif, string method, string format, params object[] args) { }
        public override void WarningHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) { }
        public override void ErrorHandler(Tiff tif, string method, string format, params object[] args) { }
        public override void ErrorHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) { }
    }
}

/// <summary>Decoded source dimensions + the normalised OCR-ready JPEG bytes.</summary>
public sealed record NormalizedImage(int Width, int Height, byte[] Jpeg);
