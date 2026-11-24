using System.Runtime.Versioning;
using Mailvec.Parsing;
using Mailvec.Pdf;

namespace Mailvec.Core.Tests.Attachments;

/// <summary>
/// The arithmetic between a PDF's own claims (page size, page count) and what
/// is handed to PDFium, tested without rendering anything.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public class PdfRenderBoundsTests
{
    [Theory]
    [InlineData(612, 792)]      // US Letter
    [InlineData(1e12, 792)]     // absurd width
    [InlineData(1e12, 1e12)]
    [InlineData(0.01, 0.01)]    // rounds to zero pixels
    [InlineData(0.01, 1e12)]    // both at once
    [InlineData(float.MaxValue, 1)]
    public void Any_positive_page_size_becomes_a_render_within_the_pixel_cap(double widthPt, double heightPt)
    {
        var (w, h) = PdfRenderer.TargetPixels(widthPt, heightPt);

        w.ShouldBeInRange(1, PdfRenderer.MaxEdgePx);
        h.ShouldBeInRange(1, PdfRenderer.MaxEdgePx);
    }

    [Fact]
    public void An_ordinary_page_keeps_its_size_and_a_large_one_keeps_its_shape()
    {
        PdfRenderer.TargetPixels(612, 792).ShouldBe((1187, PdfRenderer.MaxEdgePx)); // 1275x1650 at 150 dpi, scaled to the cap
        PdfRenderer.TargetPixels(144, 72).ShouldBe((300, 150));

        var (w, h) = PdfRenderer.TargetPixels(7200, 3600);
        (w, h).ShouldBe((PdfRenderer.MaxEdgePx, PdfRenderer.MaxEdgePx / 2));
    }

    [Theory]
    [InlineData(double.NaN, 792)]
    [InlineData(612, double.PositiveInfinity)]
    [InlineData(0, 792)]
    [InlineData(612, -5)]
    public void A_page_size_that_is_not_a_positive_number_is_refused(double widthPt, double heightPt)
    {
        Should.Throw<InvalidDataException>(() => PdfRenderer.TargetPixels(widthPt, heightPt));
    }

    [Theory]
    [InlineData(10, 0, 3, 0, 3)]
    [InlineData(10, 8, 5, 8, 10)]
    [InlineData(10, 10, int.MaxValue, 10, 10)]   // first + max used to wrap negative
    [InlineData(10, 5, int.MaxValue, 5, 10)]
    [InlineData(10, 50, 1, 10, 10)]              // past the end: empty, not negative
    [InlineData(10, -3, 2, 0, 2)]
    [InlineData(10, 0, -1, 0, 0)]
    [InlineData(-1, 0, 5, 0, 0)]
    public void The_page_range_is_computed_without_wrapping(int pageCount, int first, int max, int start, int end)
    {
        InProcessParser.PageRange(pageCount, first, max).ShouldBe((start, end));
    }
}
