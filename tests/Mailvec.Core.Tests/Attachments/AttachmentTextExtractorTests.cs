using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ax = DocumentFormat.OpenXml.Drawing;
using Px = DocumentFormat.OpenXml.Presentation;
using Sx = DocumentFormat.OpenXml.Spreadsheet;
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
    public void Extracts_xlsx_sheet_names_and_shared_strings()
    {
        var xlsxBytes = BuildSimpleXlsx("Guest List", "Alice Johnson", "Table 4", "RSVP yes");
        const string ct = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        var part = BuildMimePart(xlsxBytes, ct, "wedding.xlsx");

        var result = BuildExtractor().Extract(part, "wedding.xlsx", ct, xlsxBytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text.ShouldNotBeNull();
        result.Text!.ShouldContain("Guest List");    // sheet name
        result.Text.ShouldContain("Alice Johnson");  // shared string cell
        result.Text.ShouldContain("RSVP yes");
    }

    [Fact]
    public void Extracts_xlsx_via_octet_stream_extension_fallback()
    {
        var xlsxBytes = BuildSimpleXlsx("Budget", "Catering total 5000");
        var part = BuildMimePart(xlsxBytes, "application/octet-stream", "budget.xlsx");

        var result = BuildExtractor().Extract(part, "budget.xlsx", "application/octet-stream", xlsxBytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text!.ShouldContain("Catering total 5000");
    }

    [Fact]
    public void Extracts_pptx_slide_text_in_order()
    {
        var pptxBytes = BuildSimplePptx("Living Room Design", "Sofa options and paint colors");
        const string ct = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
        var part = BuildMimePart(pptxBytes, ct, "design.pptx");

        var result = BuildExtractor().Extract(part, "design.pptx", ct, pptxBytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text.ShouldNotBeNull();
        result.Text!.ShouldContain("Living Room Design");
        result.Text.ShouldContain("Sofa options and paint colors");
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
        "LOCATION:Example Studio\r\n" +
        "ORGANIZER;CN=Example Photography:mailto:studio@example.com\r\n" +
        "ATTENDEE;CN=\"Daniel Delaney\";ROLE=REQ-PARTICIPANT:mailto:user@example.com\r\n" +
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
        result.Text.ShouldContain("Location: Example Studio");
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

    // vCard fixture: structured ORG/N/ADR values, an escaped comma in NOTE, and
    // a base64 PHOTO blob that must be dropped rather than dumped into the index.
    private const string SampleVCard =
        "BEGIN:VCARD\r\nVERSION:3.0\r\n" +
        "FN:Jane Q. Roe\r\n" +
        "N:Roe;Jane;Q;;\r\n" +
        "ORG:Acme Corp;Widgets Division\r\n" +
        "TITLE:Chief Engineer\r\n" +
        "EMAIL;TYPE=WORK:jane@acme.example\r\n" +
        "TEL;TYPE=CELL:+1-555-0100\r\n" +
        "ADR;TYPE=HOME:;;123 Main St;Springfield;IL;62704;USA\r\n" +
        "NOTE:Met at the trade show\\, follow up in Q2\r\n" +
        "PHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQSkZJRgABAQAAAQABAAD\r\n" +
        "END:VCARD\r\n";

    [Theory]
    [InlineData("text/vcard", "jane.vcf")]
    [InlineData("text/x-vcard", "jane.vcf")]
    [InlineData("application/vcard", "jane.vcf")]
    [InlineData("application/octet-stream", "jane.vcf")]  // extension fallback
    public void Extracts_vcard_fields_across_mislabeled_types(string contentType, string fileName)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(SampleVCard);
        var part = BuildMimePart(bytes, contentType, fileName);

        var result = BuildExtractor().Extract(part, fileName, contentType, bytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text.ShouldNotBeNull();
        result.Text!.ShouldContain("Jane Q. Roe");
        result.Text.ShouldContain("Org: Acme Corp Widgets Division");
        result.Text.ShouldContain("Title: Chief Engineer");
        result.Text.ShouldContain("Email: jane@acme.example");
        result.Text.ShouldContain("Tel: +1-555-0100");
        result.Text.ShouldContain("Address: 123 Main St, Springfield, IL, 62704, USA");
        result.Text.ShouldContain("Met at the trade show, follow up in Q2");  // \, unescaped
        // The base64 PHOTO blob must be dropped, not leaked into the search text.
        result.Text.ShouldNotContain("/9j/4AAQ");
        result.Text.ShouldNotContain("BEGIN:VCARD");
    }

    [Fact]
    public void VCard_decodes_quoted_printable_and_joins_soft_breaks()
    {
        // vCard 2.1 QUOTED-PRINTABLE NOTE: =0D=0A are CR/LF, and the trailing '='
        // is a soft line break continuing the value onto the next physical line.
        var vcf =
            "BEGIN:VCARD\r\nVERSION:2.1\r\n" +
            "FN:The Century House Hotel\r\n" +
            "NOTE;ENCODING=QUOTED-PRINTABLE:Checkin Time: 15:00=0D=0ACheckout Time: 11:00=0D=0A=0D=0ADirec=\r\n" +
            "tions: turn left at Main St\r\n" +
            "END:VCARD\r\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(vcf);
        var part = BuildMimePart(bytes, "text/vcard", "hotel.vcf");

        var result = BuildExtractor().Extract(part, "hotel.vcf", "text/vcard", bytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text.ShouldNotBeNull();
        result.Text!.ShouldContain("Checkin Time: 15:00");
        result.Text.ShouldContain("Checkout Time: 11:00");
        result.Text.ShouldContain("Directions: turn left at Main St");  // soft-break tail recovered
        result.Text.ShouldNotContain("=0D=0A");                          // QP escapes decoded
        result.Text.ShouldNotContain("Direc=");                          // no dangling soft-break marker
    }

    [Fact]
    public void VCard_with_no_meaningful_properties_returns_no_text()
    {
        // Only VERSION + a PHOTO blob — nothing in our kept-field set, so there's
        // no searchable text (the shape behind the lone 'no_text' vCard row).
        var vcf =
            "BEGIN:VCARD\r\nVERSION:3.0\r\n" +
            "PHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQSkZJRgABAQAAAQABAAD\r\n" +
            "END:VCARD\r\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(vcf);
        var part = BuildMimePart(bytes, "text/vcard", "photoonly.vcf");

        var result = BuildExtractor().Extract(part, "photoonly.vcf", "text/vcard", bytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusNoText);
        result.Text.ShouldBeNull();
    }

    [Fact]
    public void VCard_falls_back_to_structured_name_when_fn_absent()
    {
        var vcf =
            "BEGIN:VCARD\r\nVERSION:3.0\r\n" +
            "N:Smith;John;;;\r\n" +
            "EMAIL:john@smith.example\r\n" +
            "END:VCARD\r\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(vcf);
        var part = BuildMimePart(bytes, "text/vcard", "john.vcf");

        var result = BuildExtractor().Extract(part, "john.vcf", "text/vcard", bytes.Length);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text.ShouldNotBeNull();
        result.Text.ShouldContain("Smith John");   // N joined as the display-name fallback
        result.Text.ShouldContain("Email: john@smith.example");
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

    /// <summary>Real .xlsx with one named sheet and the given cell texts interned
    /// in the shared-string table (how Excel stores text).</summary>
    private static byte[] BuildSimpleXlsx(string sheetName, params string[] cellTexts)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new Sx.Workbook();
            var sheets = wbPart.Workbook.AppendChild(new Sx.Sheets());

            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            wsPart.Worksheet = new Sx.Worksheet(new Sx.SheetData());

            var sstPart = wbPart.AddNewPart<SharedStringTablePart>();
            sstPart.SharedStringTable = new Sx.SharedStringTable();
            foreach (var t in cellTexts)
            {
                sstPart.SharedStringTable.AppendChild(new Sx.SharedStringItem(new Sx.Text(t)));
            }

            sheets.AppendChild(new Sx.Sheet
            {
                Id = wbPart.GetIdOfPart(wsPart),
                SheetId = 1,
                Name = sheetName,
            });
        }
        return ms.ToArray();
    }

    /// <summary>Real .pptx with one slide per given string; the text sits in a
    /// Drawing a:t run inside a shape's text body (where slide text lives).</summary>
    private static byte[] BuildSimplePptx(params string[] slideTexts)
    {
        using var ms = new MemoryStream();
        using (var doc = PresentationDocument.Create(ms, PresentationDocumentType.Presentation))
        {
            var presPart = doc.AddPresentationPart();
            var slideIdList = new Px.SlideIdList();
            uint sid = 256;

            foreach (var txt in slideTexts)
            {
                var slidePart = presPart.AddNewPart<SlidePart>();
                slidePart.Slide = new Px.Slide(
                    new Px.CommonSlideData(new Px.ShapeTree(
                        new Px.NonVisualGroupShapeProperties(
                            new Px.NonVisualDrawingProperties { Id = 1, Name = "" },
                            new Px.NonVisualGroupShapeDrawingProperties(),
                            new Px.ApplicationNonVisualDrawingProperties()),
                        new Px.GroupShapeProperties(),
                        new Px.Shape(
                            new Px.NonVisualShapeProperties(
                                new Px.NonVisualDrawingProperties { Id = 2, Name = "TextBox" },
                                new Px.NonVisualShapeDrawingProperties(),
                                new Px.ApplicationNonVisualDrawingProperties()),
                            new Px.ShapeProperties(),
                            new Px.TextBody(
                                new Ax.BodyProperties(),
                                new Ax.ListStyle(),
                                new Ax.Paragraph(new Ax.Run(new Ax.Text(txt))))))));

                slideIdList.AppendChild(new Px.SlideId { Id = sid++, RelationshipId = presPart.GetIdOfPart(slidePart) });
            }

            presPart.Presentation = new Px.Presentation(slideIdList);
        }
        return ms.ToArray();
    }
}
