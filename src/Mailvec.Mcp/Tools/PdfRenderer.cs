using System.Runtime.Versioning;
using PDFtoImage;

namespace Mailvec.Mcp.Tools;

/// <summary>
/// Rasterises a single PDF page to PNG via PDFtoImage (PDFium + SkiaSharp,
/// native). Kept apart from the MCP tool so it can be unit-tested directly —
/// the test doubles as the "does the native lib load on this RID" check.
///
/// PDFium is not thread-safe; PDFtoImage serialises all calls into it
/// internally, so callers don't add their own lock. 150 DPI keeps a
/// letter/A4 page comfortably under Claude's image dimension cap (it scales
/// anything larger down anyway) while staying legible for tables.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
internal static class PdfRenderer
{
    private const int Dpi = 150;

    /// <summary>Number of pages in the PDF. Throws if the bytes aren't a PDF PDFium can open.</summary>
    public static int PageCount(byte[] pdf) => Conversion.GetPageCount(pdf);

    /// <summary>Render a single 0-based page index to PNG bytes.</summary>
    public static byte[] RenderPagePng(byte[] pdf, int pageIndex)
    {
        using var ms = new MemoryStream();
        Conversion.SavePng(ms, pdf, page: pageIndex, options: new RenderOptions(Dpi: Dpi));
        return ms.ToArray();
    }
}
