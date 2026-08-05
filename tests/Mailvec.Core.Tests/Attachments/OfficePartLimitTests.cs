using System.IO.Compression;
using System.Text;
using Mailvec.Core.Attachments;
using Mailvec.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailvec.Core.Tests.Attachments;

/// <summary>
/// Office formats are ZIP containers, so <c>Indexer:AttachmentMaxBytes</c> bounds
/// the compressed input and says nothing about how far the XML expands. Measured
/// before the fix: a 0.95 MB DOCX whose document.xml is 278 MB allocated 2,451 MB
/// and took 9.1 s — past the indexer's 2 GB container limit at 1/26th of the
/// 25 MB input ceiling, and a wedge rather than a one-off crash, since extraction
/// runs before any row is written and the restarted indexer re-parses the same
/// file forever.
///
/// Each format gets a PAIR: a small package that must extract normally, and a
/// bomb built from the same template that must not. Without the small half, a
/// bomb test passes just as well when the hand-built package is malformed — the
/// status would be 'failed' either way and the limit would never be exercised.
/// </summary>
public class OfficePartLimitTests
{
    private static AttachmentTextExtractor Extractor() =>
        new(Microsoft.Extensions.Options.Options.Create(new IndexerOptions()), NullLogger<AttachmentTextExtractor>.Instance);

    private static ExtractionResult Extract(byte[] package, string fileName, string contentType)
    {
        var part = new MimePart(contentType)
        {
            Content = new MimeContent(new MemoryStream(package)),
            FileName = fileName,
        };
        return Extractor().Extract(part, fileName, contentType, package.LongLength);
    }

    // Comfortably over MaxCharactersInOfficePart (32M) once expanded, while the
    // compressed package stays a fraction of AttachmentMaxBytes — which is the
    // whole point: the input gate cannot see this coming.
    private const int BombRepeats = 900_000;
    private const int SmallRepeats = 5;

    [Fact]
    public void The_office_part_limit_is_not_zero()
    {
        // 0 is the SDK's "unlimited" and the pre-fix behaviour, so a well-meaning
        // "reset to default" is indistinguishable from reintroducing the bug.
        AttachmentTextExtractor.OfficeOpenSettings.MaxCharactersInPart.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_normal_docx_extracts()
    {
        var result = Extract(Docx(SmallRepeats), "small.docx", DocxType);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text.ShouldNotBeNull().ShouldContain("MAILVEC");
    }

    [Fact]
    public void A_docx_whose_xml_expands_past_the_part_limit_fails_instead_of_expanding()
    {
        var package = Docx(BombRepeats);
        package.Length.ShouldBeLessThan(25 * 1024 * 1024); // passes the input gate

        var result = Extract(package, "bomb.docx", DocxType);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusFailed);
        result.Text.ShouldBeNull();
    }

    [Fact]
    public void A_normal_xlsx_extracts()
    {
        var result = Extract(Xlsx(SmallRepeats), "small.xlsx", XlsxType);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text.ShouldNotBeNull().ShouldContain("MAILVEC");
    }

    [Fact]
    public void An_xlsx_whose_xml_expands_past_the_part_limit_fails_instead_of_expanding()
    {
        var package = Xlsx(BombRepeats);
        package.Length.ShouldBeLessThan(25 * 1024 * 1024);

        var result = Extract(package, "bomb.xlsx", XlsxType);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusFailed);
        result.Text.ShouldBeNull();
    }

    [Fact]
    public void A_normal_pptx_extracts()
    {
        var result = Extract(Pptx(SmallRepeats), "small.pptx", PptxType);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        result.Text.ShouldNotBeNull().ShouldContain("MAILVEC");
    }

    [Fact]
    public void A_pptx_whose_xml_expands_past_the_part_limit_fails_instead_of_expanding()
    {
        var package = Pptx(BombRepeats);
        package.Length.ShouldBeLessThan(25 * 1024 * 1024);

        var result = Extract(package, "bomb.pptx", PptxType);

        result.Status.ShouldBe(AttachmentTextExtractor.StatusFailed);
        result.Text.ShouldBeNull();
    }

    // ── Hand-built packages ──────────────────────────────────────────────────
    // Built by hand rather than through the SDK's authoring API because the
    // point is a part far larger than anything we would assemble node by node.

    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string PptxType = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    private const string RelsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string CtNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string OfficeDocRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static string Repeat(string unit, int times) => string.Concat(Enumerable.Repeat(unit, times));

    private static byte[] Docx(int repeats) => Zip(
        ("[Content_Types].xml",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="{CtNs}"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="{DocxType}.main+xml"/></Types>"""),
        ("_rels/.rels",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelsNs}"><Relationship Id="rId1" Type="{OfficeDocRel}" Target="word/document.xml"/></Relationships>"""),
        ("word/document.xml",
         """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>"""
         + Repeat("<w:p><w:r><w:t>MAILVEC PARAGRAPH TEXT CONTENT PADDING</w:t></w:r></w:p>", repeats)
         + "</w:body></w:document>"));

    private static byte[] Xlsx(int repeats) => Zip(
        ("[Content_Types].xml",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="{CtNs}"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/xl/workbook.xml" ContentType="{XlsxType}.main+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/></Types>"""),
        ("_rels/.rels",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelsNs}"><Relationship Id="rId1" Type="{OfficeDocRel}" Target="xl/workbook.xml"/></Relationships>"""),
        ("xl/_rels/workbook.xml.rels",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelsNs}"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>"""),
        ("xl/workbook.xml",
         """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheets><sheet name="MAILVEC SHEET" sheetId="1" r:id="rId9" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"/></sheets></workbook>"""),
        ("xl/sharedStrings.xml",
         """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">"""
         + Repeat("<si><t>MAILVEC SHARED STRING CELL TEXT PADDING</t></si>", repeats)
         + "</sst>"));

    private static byte[] Pptx(int repeats) => Zip(
        ("[Content_Types].xml",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="{CtNs}"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/ppt/presentation.xml" ContentType="{PptxType}.main+xml"/><Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/></Types>"""),
        ("_rels/.rels",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelsNs}"><Relationship Id="rId1" Type="{OfficeDocRel}" Target="ppt/presentation.xml"/></Relationships>"""),
        ("ppt/_rels/presentation.xml.rels",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelsNs}"><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/></Relationships>"""),
        ("ppt/presentation.xml",
         """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:presentation xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:sldIdLst><p:sldId id="256" r:id="rId2"/></p:sldIdLst></p:presentation>"""),
        ("ppt/slides/slide1.xml",
         """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:cSld><p:spTree><p:sp><p:txBody>"""
         + Repeat("<a:p><a:r><a:t>MAILVEC SLIDE RUN TEXT CONTENT PADDING</a:t></a:r></a:p>", repeats)
         + "</p:txBody></p:sp></p:spTree></p:cSld></p:sld>"));
}
