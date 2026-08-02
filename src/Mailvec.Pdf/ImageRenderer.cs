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
///
/// Every format is gated on <see cref="MaxDecodedPixels"/> <em>before</em> a
/// pixel buffer is allocated — see that constant for why the encoded size is
/// not a proxy for the peak.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public static class ImageRenderer
{
    private const int JpegQuality = 85;

    /// <summary>
    /// Decompression-bomb ceiling, in decoded pixels, applied to every format.
    ///
    /// The decode allocates width*height*4 bytes, and the *encoded* size says
    /// nothing about that peak: PNG, WEBP and TIFF all compress a uniform raster
    /// enormously, so a file well under the indexer's 25 MB AttachmentMaxBytes
    /// can expand to many GB. That matters here more than in most decode paths
    /// because the bytes are attacker-chosen (anyone can mail an attachment) and
    /// the embedder's OCR pass feeds them in <em>unattended</em>.
    ///
    /// 40 MP ≈ 160 MB of raster — well above any real scan, and above a phone
    /// photo. Raising it raises the worst-case peak RSS of the OCR pass by 4
    /// bytes per pixel, against the embedder's 2g compose <c>mem_limit</c>; move
    /// one without checking the other and a legitimate scan starts OOM-killing
    /// the container. See docs/security.md "Container hardening".
    /// </summary>
    public const long MaxDecodedPixels = 40_000_000;

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
    /// Returns null when the bytes aren't a decodable image, or when the decoded
    /// raster would exceed <see cref="MaxDecodedPixels"/> — the caller marks the
    /// attachment terminally so it isn't re-selected every cycle. The reported
    /// <see cref="NormalizedImage.Width"/>/<see cref="NormalizedImage.Height"/>
    /// are the *source* dimensions (pre-downscale): that's what the dimension /
    /// aspect-ratio gate keys off.
    /// </summary>
    public static NormalizedImage? TryNormalize(byte[] bytes) =>
        TryNormalize(bytes, MaxDecodedPixels);

    /// <summary>
    /// Ceiling-injecting overload, for tests that need to trip the bomb guard
    /// without materialising a genuinely 40-megapixel fixture.
    /// </summary>
    internal static NormalizedImage? TryNormalize(byte[] bytes, long maxDecodedPixels)
    {
        // The try/catch is the backstop, not the guard: Decode returns null for
        // the expected rejections (no codec for these bytes, over the pixel
        // ceiling), but LibTiff and the JPEG encode can still throw on malformed
        // input. Treat any failure as "not an OCR-able raster" and return null,
        // so the caller marks the attachment failed instead of the whole OCR
        // batch aborting and retrying the poison bytes forever.
        try
        {
            using var src = Decode(bytes, maxDecodedPixels);
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

    /// <summary>
    /// Decode to an <see cref="SKBitmap"/>, routing TIFF to LibTiff since
    /// SkiaSharp can't, and refusing anything over <paramref name="maxDecodedPixels"/>
    /// before a pixel buffer is allocated.
    /// </summary>
    private static SKBitmap? Decode(byte[] bytes, long maxDecodedPixels)
    {
        if (IsTiff(bytes)) return DecodeTiff(bytes, maxDecodedPixels);
        return DecodeRaster(bytes, maxDecodedPixels);
    }

    /// <summary>
    /// JPEG/PNG/GIF/BMP/WEBP via SkiaSharp, through <see cref="SKCodec"/> rather
    /// than <c>SKBitmap.Decode(byte[])</c>.
    ///
    /// The codec exposes the header's dimensions without decoding pixels, which
    /// is the only place the bomb guard can run: <c>SKBitmap.Decode(byte[])</c>
    /// commits to the full width*height*4 allocation before returning anything
    /// to check. Creating the codec explicitly also turns "SkiaSharp's native
    /// build has no codec for these bytes" (HEIC, corrupt JPEG) into a plain
    /// null, where <c>SKBitmap.Decode(byte[])</c> threw
    /// <c>ArgumentNullException("codec")</c> for TryNormalize's catch to mop up.
    /// </summary>
    private static SKBitmap? DecodeRaster(byte[] bytes, long maxDecodedPixels)
    {
        // CreateCopy duplicates into native memory (bounded by the caller's own
        // read, ≤ AttachmentMaxBytes for the OCR path) and is freed on dispose.
        // The alternative — SKCodec.Create(Stream) — makes the codec's lifetime
        // depend on a managed stream outliving it, for no real saving here.
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) return null;
        // long multiply: int would overflow at 46341² and wrap to a value that
        // passes the ceiling — the exact input this guard exists to stop.
        if ((long)info.Width * info.Height > maxDecodedPixels) return null;

        return SKBitmap.Decode(codec);
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

    private static SKBitmap? DecodeTiff(byte[] bytes, long maxDecodedPixels)
    {
        using var ms = new MemoryStream(bytes);
        using var tiff = Tiff.ClientOpen("mem", "r", ms, new TiffStream());
        if (tiff is null) return null;

        // The IFD tags are the header equivalent of SKCodec.Info: dimensions
        // before any raster allocation. Same ceiling, same reason.
        int width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        int height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        if (width <= 0 || height <= 0 || (long)width * height > maxDecodedPixels) return null;

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
