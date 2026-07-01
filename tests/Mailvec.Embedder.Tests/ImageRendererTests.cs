using System.Runtime.Versioning;
using Mailvec.Pdf;
using SkiaSharp;

namespace Mailvec.Embedder.Tests;

/// <summary>
/// Guards the contract that <see cref="ImageRenderer.TryNormalize"/> returns null
/// (never throws) on input it can't turn into an OCR-able raster. Regression for
/// the overnight backlog stall: SkiaSharp's native build has no TIFF/HEIC codec,
/// and <c>SKBitmap.Decode(byte[])</c> throws ArgumentNullException("codec")
/// rather than returning null on such bytes — which escaped the OCR pass's
/// per-item handling and made it retry the poison attachment forever.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public class ImageRendererTests
{
    // A real, decodable PNG (generated so the test never depends on a hand-typed
    // base64 blob being valid).
    private static byte[] MakePng(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.CornflowerBlue);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void TryNormalize_returns_null_on_undecodable_bytes()
    {
        // Garbage / an unsupported codec (TIFF, HEIC) drives SKBitmap.Decode down
        // the throwing path — TryNormalize must swallow it and return null.
        ImageRenderer.TryNormalize([0x49, 0x49, 0x2A, 0x00, 1, 2, 3, 4, 5, 6, 7, 8]).ShouldBeNull();
        ImageRenderer.TryNormalize(new byte[512]).ShouldBeNull();
    }

    [Fact]
    public void TryNormalize_returns_null_on_empty_input()
    {
        ImageRenderer.TryNormalize([]).ShouldBeNull();
    }

    [Fact]
    public void TryNormalize_decodes_a_supported_image()
    {
        var result = ImageRenderer.TryNormalize(MakePng(48, 32));

        result.ShouldNotBeNull();
        result.Width.ShouldBe(48);
        result.Height.ShouldBe(32);
        result.Jpeg.Length.ShouldBeGreaterThan(0);
    }
}
