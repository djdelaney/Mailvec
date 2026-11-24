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

    // Decompression-bomb guard. Both fixtures are genuinely decodable and tiny
    // on the wire — the point is that a *small file* declaring a large raster is
    // refused on its header, before width*height*4 bytes are allocated. The
    // ceiling is injected rather than the fixture inflated: materialising a real
    // 40-megapixel image to trip the production constant would cost 160 MB and
    // several seconds per run, and would prove the same thing.
    //
    // 1536 px (48*32) against a 100 px ceiling stands in for 25 MB of PNG
    // against 40 MP. The paired "still normalises under the ceiling" assertion
    // is what stops this passing for the wrong reason: TryNormalize funnels
    // every failure to null, so a fixture that simply didn't decode would look
    // identical to one the guard caught.

    [Fact]
    public void TryNormalize_rejects_a_raster_over_the_pixel_ceiling()
    {
        var png = MakePng(48, 32); // 1536 px

        ImageRenderer.TryNormalize(png, maxDecodedPixels: 100).ShouldBeNull();
        ImageRenderer.TryNormalize(png, maxDecodedPixels: 2000).ShouldNotBeNull();
    }

    [Fact]
    public void TryNormalize_rejects_a_tiff_over_the_pixel_ceiling()
    {
        var tiff = MakeTiff(64, 40); // 2560 px

        ImageRenderer.TryNormalize(tiff, maxDecodedPixels: 100).ShouldBeNull();
        ImageRenderer.TryNormalize(tiff, maxDecodedPixels: 3000).ShouldNotBeNull();
    }

    /// <summary>
    /// A hand-written 150-byte little-endian TIFF: a 64x64 image whose tile
    /// size, samples per pixel and bits per sample are the caller's. Written
    /// by hand because LibTiff's writer refuses to produce the hostile ones.
    /// </summary>
    private static byte[] MakeTiledTiff(uint tileEdge, uint samplesPerPixel = 1, uint bitsPerSample = 8, uint tileDepth = 1)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)'I'); w.Write((byte)'I'); w.Write((ushort)42); w.Write((uint)24);
        w.Write(new byte[16]); // the "pixel data", at offset 8
        List<(ushort Tag, ushort Type, uint Value)> tags =
        [
            (256, 4, 64), (257, 4, 64), (258, 3, bitsPerSample), (259, 3, 1), (262, 3, 1),
            (277, 3, samplesPerPixel), (322, 4, tileEdge), (323, 4, tileEdge), (324, 4, 8), (325, 4, 16),
        ];
        if (tileDepth != 1) tags.Add((32998, 4, tileDepth)); // TILEDEPTH
        w.Write((ushort)tags.Count);
        foreach (var (tag, type, value) in tags) { w.Write(tag); w.Write(type); w.Write((uint)1); w.Write(value); }
        w.Write((uint)0);
        w.Flush();
        return ms.ToArray();
    }

    // The pixel ceiling reads two tags; LibTiff sizes its decode buffer from
    // four others. Before the buffer check, the 32768 case allocated 2 GB for
    // a 64x64 image and then decoded it — so "returns null" alone would not
    // prove much, and the allocation is asserted too. The first case is the
    // control: the same file with a sane tile size must still decode, or a
    // fixture LibTiff simply cannot read would pass every row below.
    [Theory]
    [InlineData(16u, 1u, 8u, 1u, true)]
    [InlineData(32768u, 1u, 8u, 1u, false)]   // 1 GB tile
    [InlineData(16384u, 4u, 8u, 1u, false)]   // 1 GB tile through samples per pixel
    [InlineData(65536u, 1u, 8u, 1u, false)]   // tile area that wraps a 32-bit product
    [InlineData(16u, 60000u, 8u, 1u, false)]  // absurd channel count
    [InlineData(16u, 1u, 64u, 1u, false)]     // a depth the RGBA reader cannot decode
    [InlineData(16u, 1u, 8u, 65536u, false)]  // a 3-D tile: a third multiplier
    public void A_tiff_whose_tags_size_a_huge_decode_buffer_is_refused_without_allocating_it(
        uint tileEdge, uint samples, uint bits, uint tileDepth, bool decodes)
    {
        var tiff = MakeTiledTiff(tileEdge, samples, bits, tileDepth);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = ImageRenderer.TryNormalize(tiff);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        (result is not null).ShouldBe(decodes);
        allocated.ShouldBeLessThan(16L * 1024 * 1024);
    }

    [Fact]
    public void MaxDecodedPixels_bounds_the_raster_a_single_attachment_can_allocate()
    {
        // 4 bytes per pixel is the decode allocation; this is the number that
        // has to stay survivable on the Docker VM, so pin it rather than let it
        // drift upward one "just this once" at a time.
        (ImageRenderer.MaxDecodedPixels * 4).ShouldBeLessThanOrEqualTo(256L * 1024 * 1024);
    }
}
