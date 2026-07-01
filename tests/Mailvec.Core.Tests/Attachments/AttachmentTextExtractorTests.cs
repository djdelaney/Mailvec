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

    // RFC 5545 fixture: CRLF line endings, a folded DESCRIPTION whose fold
    // splits "$225.00" into "$225.0" + " 0" (the corruption the raw text path
    // produced), an escaped comma, and a CN-bearing ATTENDEE.
    private const string SampleIcs =
        "BEGIN:VCALENDAR\r\n" +
        "VERSION:2.0\r\n" +
        "PRODID:-//Example//EN\r\n" +
        "BEGIN:VEVENT\r\n" +
        "UID:1526757496@scheduling\r\n" +
        "DTSTAMP:20250824T002332Z\r\n" +
        "DTSTART;TZID=America/New_York:20251205T140000\r\n" +
        "SUMMARY:Mini Session\r\n" +
        "LOCATION:Taproot Studio\r\n" +
        "ORGANIZER;CN=Taproot Photography:mailto:studio@example.com\r\n" +
        "ATTENDEE;CN=\"Daniel Delaney\";ROLE=REQ-PARTICIPANT:mailto:dan@hactar.com\r\n" +
        "DESCRIPTION:Name: Daniel Delaney\\nPrice: $225.0\r\n" +
        " 0\\, paid online\r\n" +
        "END:VEVENT\r\n" +
        "END:VCALENDAR\r\n";

    [Theory]
    [InlineData("text/calendar", "invite.ics")]
    [InlineData("application/ics", "invite.ics")]
    [InlineData("application/calendar", "invite.ics")]
    [InlineData("application/octet-stream", "invite.ics")]  // extension fallback
    [InlineData("text/plain", "reservation.ics")]           // .ics must beat generic text/
    public void Extracts_calendar_fields_across_mislabeled_types(string contentType, string fileName)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(SampleIcs);
        var part = BuildMimePart(bytes, contentType, fileName);

        var result = BuildExtractor().Extract(part, fileName, contentType, bytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text.ShouldNotBeNull();
        result.Text!.ShouldContain("Mini Session");
        result.Text.ShouldContain("Location: Taproot Studio");
        result.Text.ShouldContain("Daniel Delaney");
        // Machine noise must be gone.
        result.Text.ShouldNotContain("DTSTAMP");
        result.Text.ShouldNotContain("1526757496@scheduling");
    }

    [Fact]
    public void Calendar_unfolds_folded_lines_without_corrupting_tokens()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(SampleIcs);
        var part = BuildMimePart(bytes, "text/calendar", "invite.ics");

        var result = BuildExtractor().Extract(part, "invite.ics", "text/calendar", bytes.Length);

        // Unfolding rejoins "$225.0" + " 0" into "$225.00"; the raw text path
        // left an injected space ("$225.0 0"). Escaped comma is unescaped.
        result.Text.ShouldNotBeNull();
        result.Text!.ShouldContain("$225.00");
        result.Text.ShouldContain("$225.00, paid online");
        result.Text.ShouldNotContain("$225.0 0");
    }

    [Fact]
    public void Calendar_falls_back_to_windows1252_for_non_utf8_bytes()
    {
        // 0x92 is windows-1252 for a curly apostrophe and a lone (invalid)
        // UTF-8 continuation byte, so strict UTF-8 decode fails and the 1252
        // fallback engages. Regression: this used to throw ArgumentException
        // because CodePagesEncodingProvider wasn't registered.
        var ascii = System.Text.Encoding.ASCII;
        var bytes = new List<byte>();
        bytes.AddRange(ascii.GetBytes("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:Dan"));
        bytes.Add(0x92);
        bytes.AddRange(ascii.GetBytes("s Meeting\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"));
        var part = BuildMimePart(bytes.ToArray(), "text/calendar", "invite.ics");

        var result = BuildExtractor().Extract(part, "invite.ics", "text/calendar", bytes.Count);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text.ShouldNotBeNull();
        result.Text!.ShouldContain("Dan’s Meeting");
    }

    [Fact]
    public void Calendar_with_no_meaningful_properties_returns_no_text()
    {
        // VCALENDAR wrapper + VTIMEZONE only — no summary/location/dtstart/etc.
        // (this is the shape behind the handful of 'no_text' calendar rows).
        var ics =
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//x//EN\r\n" +
            "BEGIN:VTIMEZONE\r\nTZID:UTC\r\nEND:VTIMEZONE\r\n" +
            "END:VCALENDAR\r\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(ics);
        var part = BuildMimePart(bytes, "text/calendar", "empty.ics");

        var result = BuildExtractor().Extract(part, "empty.ics", "text/calendar", bytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusNoText);
        result.Text.ShouldBeNull();
    }

    [Fact]
    public void Calendar_strips_mailto_when_participant_has_no_cn()
    {
        var ics =
            "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\n" +
            "SUMMARY:Standup\r\n" +
            "ORGANIZER:mailto:boss@example.com\r\n" +
            "END:VEVENT\r\nEND:VCALENDAR\r\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(ics);
        var part = BuildMimePart(bytes, "text/calendar", "standup.ics");

        var result = BuildExtractor().Extract(part, "standup.ics", "text/calendar", bytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text.ShouldNotBeNull();
        result.Text!.ShouldContain("Organizer: boss@example.com");  // bare mailto value, prefix stripped
        result.Text.ShouldNotContain("mailto:");
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
