using System.Runtime.Versioning;
using BitMiracle.LibTiff.Classic;
using Mailvec.Pdf;
using SkiaSharp;

namespace Mailvec.Embedder.Tests;

/// <summary>
/// Guards <see cref="ImageRenderer.TryNormalize"/>: it must return null (never
/// throw) on input it can't rasterise, and it must decode TIFF — which
/// SkiaSharp's native build has no codec for — via the managed LibTiff path.
/// Regression for the overnight backlog stall, where <c>SKBitmap.Decode</c>
/// threw on TIFF/HEIC and the poison attachment retried forever.
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

    // A real uncompressed RGB TIFF, written in-process so the fixture is valid on
    // every platform (SkiaSharp cannot encode TIFF, so we can't round-trip via it).
    private static byte[] MakeTiff(int w, int h)
    {
        var ms = new MemoryStream();
        using (var tif = Tiff.ClientOpen("mem", "w", ms, new TiffStream()))
        {
            tif.SetField(TiffTag.IMAGEWIDTH, w);
            tif.SetField(TiffTag.IMAGELENGTH, h);
            tif.SetField(TiffTag.SAMPLESPERPIXEL, 3);
            tif.SetField(TiffTag.BITSPERSAMPLE, 8);
            tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
            tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
            tif.SetField(TiffTag.ROWSPERSTRIP, h);
            tif.SetField(TiffTag.COMPRESSION, Compression.NONE);
            var row = new byte[w * 3];
            for (int x = 0; x < w; x++) { row[x * 3] = 40; row[x * 3 + 1] = 90; row[x * 3 + 2] = 160; }
            for (int y = 0; y < h; y++) tif.WriteScanline(row, y);
        }
        return ms.ToArray();
    }

    [Fact]
    public void TryNormalize_returns_null_on_undecodable_bytes()
    {
        // A TIFF magic prefix over garbage (LibTiff can't open it) and a
        // codec-less blob must both come back null, not throw.
        ImageRenderer.TryNormalize([0x49, 0x49, 0x2A, 0x00, 1, 2, 3, 4, 5, 6, 7, 8]).ShouldBeNull();
        ImageRenderer.TryNormalize(new byte[512]).ShouldBeNull();
    }

    [Fact]
    public void TryNormalize_decodes_a_tiff_via_libtiff()
    {
        var result = ImageRenderer.TryNormalize(MakeTiff(64, 40));

        result.ShouldNotBeNull();
        result.Width.ShouldBe(64);
        result.Height.ShouldBe(40);
        result.Jpeg.Length.ShouldBeGreaterThan(0);
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
