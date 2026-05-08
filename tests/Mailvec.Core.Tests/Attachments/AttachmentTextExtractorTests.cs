using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Mailvec.Core.Attachments;
using Mailvec.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using UglyToad.PdfPig.Writer;

namespace Mailvec.Core.Tests.Attachments;

public class AttachmentTextExtractorTests
{
    private static AttachmentTextExtractor BuildExtractor(long maxBytes = 25 * 1024 * 1024)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new IndexerOptions { AttachmentMaxBytes = maxBytes });
        return new AttachmentTextExtractor(opts, NullLogger<AttachmentTextExtractor>.Instance);
    }

    private static MimePart BuildMimePart(byte[] bytes, string contentType, string fileName)
    {
        var ct = MimeKit.ContentType.Parse(contentType);
        var part = new MimePart(ct)
        {
            Content = new MimeContent(new MemoryStream(bytes)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName },
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        return part;
    }

    [Fact]
    public void Extracts_plain_text_attachment_as_utf8()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("Hello, this is the attached text body.");
        var part = BuildMimePart(bytes, "text/plain", "note.txt");

        var result = BuildExtractor().Extract(part, "note.txt", "text/plain", bytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text.ShouldNotBeNull();
        result.Text!.ShouldContain("attached text body");
    }

    [Fact]
    public void Extracts_pdf_text_using_pdfpig()
    {
        // Build a minimal valid PDF in-memory with two readable lines.
        var pdfBytes = BuildSimplePdf("Quarterly revenue summary", "Q3 totals: $4.2M");
        var part = BuildMimePart(pdfBytes, "application/pdf", "report.pdf");

        var result = BuildExtractor().Extract(part, "report.pdf", "application/pdf", pdfBytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text.ShouldNotBeNull();
        result.Text!.ShouldContain("Quarterly revenue summary");
        result.Text.ShouldContain("Q3 totals");
    }

    [Fact]
    public void Extracts_docx_text_walking_paragraphs()
    {
        var docxBytes = BuildSimpleDocx("First paragraph.", "Second paragraph with key term: aardvark.");
        var part = BuildMimePart(docxBytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "memo.docx");

        var result = BuildExtractor().Extract(part, "memo.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", docxBytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text.ShouldNotBeNull();
        result.Text!.ShouldContain("First paragraph");
        result.Text.ShouldContain("aardvark");
    }

    [Fact]
    public void Returns_unsupported_for_zip_and_other_binary_types()
    {
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 };  // ZIP magic
        var part = BuildMimePart(bytes, "application/zip", "archive.zip");

        var result = BuildExtractor().Extract(part, "archive.zip", "application/zip", bytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusUnsupported);
        result.Text.ShouldBeNull();
    }

    [Fact]
    public void Returns_oversize_when_declared_size_exceeds_cap()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var part = BuildMimePart(bytes, "application/pdf", "huge.pdf");

        // Tiny cap of 1 byte; declared 1KB triggers the pre-decode skip.
        var result = BuildExtractor(maxBytes: 1).Extract(part, "huge.pdf", "application/pdf", declaredSize: 1024);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusOversize);
        result.Text.ShouldBeNull();
    }

    [Fact]
    public void Returns_failed_for_corrupt_pdf_bytes()
    {
        var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0xDE, 0xAD };  // %PDF then garbage
        var part = BuildMimePart(bytes, "application/pdf", "corrupt.pdf");

        var result = BuildExtractor().Extract(part, "corrupt.pdf", "application/pdf", bytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusFailed);
        result.Text.ShouldBeNull();
    }

    [Fact]
    public void Falls_back_to_extension_when_content_type_is_octet_stream()
    {
        var pdfBytes = BuildSimplePdf("Hidden behind octet-stream", "");
        var part = BuildMimePart(pdfBytes, "application/octet-stream", "stealth.pdf");

        var result = BuildExtractor().Extract(part, "stealth.pdf", "application/octet-stream", pdfBytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text!.ShouldContain("Hidden behind octet-stream");
    }

    /// <summary>
    /// PdfPig provides a builder we can use to synthesise a real PDF without
    /// shipping a binary fixture in the repo.
    /// </summary>
    private static byte[] BuildSimplePdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(595, 842);  // A4
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        double y = 800;
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            page.AddText(line, 12, new UglyToad.PdfPig.Core.PdfPoint(50, y), font);
            y -= 20;
        }
        return builder.Build();
    }

    /// <summary>
    /// Minimal valid DOCX produced via the OpenXml SDK — no fixture file.
    /// </summary>
    private static byte[] BuildSimpleDocx(params string[] paragraphs)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body());
            foreach (var text in paragraphs)
            {
                main.Document.Body!.AppendChild(new Paragraph(new Run(new Text(text))));
            }
        }
        return ms.ToArray();
    }
}
