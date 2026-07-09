using Mailvec.Core.Data;
using Mailvec.Core.Parsing;
using Mailvec.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Mailvec.Mcp.Tests.Tools;

/// <summary>
/// get_attachment_text is a pure DB read (no Maildir, no download dir), so the
/// tests just seed attachments.extracted_text via Upsert and assert the
/// returned content blocks. Covers the done path, each not-extractable status,
/// and the id/messageId/partIndex guards shared with the other tools.
/// </summary>
public class GetAttachmentTextToolTests
{
    private static GetAttachmentTextTool Build(TempDatabase db) =>
        new(new MessageRepository(db.Connections), Helpers.NoopLogger());

    private static long Seed(MessageRepository repo, string id, string? text, string? status, string fileName = "report.pdf")
    {
        var parsed = Helpers.Sample(id, attachments: [
            new ParsedAttachment(0, fileName, "application/pdf", 1234L, ExtractedText: text, ExtractionStatus: status),
        ]);
        return repo.Upsert(parsed, "INBOX", "INBOX/cur", id + ".eml", DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Throws_when_neither_id_nor_messageId_provided()
    {
        using var db = new TempDatabase();
        Should.Throw<McpException>(() => Build(db).GetAttachmentText(partIndex: 0));
    }

    [Fact]
    public void Throws_when_both_id_and_messageId_provided()
    {
        using var db = new TempDatabase();
        var ex = Should.Throw<McpException>(() => Build(db).GetAttachmentText(partIndex: 0, id: 1, messageId: "x@y"));
        ex.Message.ShouldContain("OR");
    }

    [Fact]
    public void Throws_when_message_does_not_exist()
    {
        using var db = new TempDatabase();
        Should.Throw<McpException>(() => Build(db).GetAttachmentText(partIndex: 0, id: 999));
    }

    [Fact]
    public void Throws_when_message_is_soft_deleted()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "del@x", "some text", "done");
        repo.MarkDeleted([id], DateTimeOffset.UtcNow);

        var ex = Should.Throw<McpException>(() => Build(db).GetAttachmentText(partIndex: 0, id: id));
        ex.Message.ShouldContain("soft-deleted");
    }

    [Fact]
    public void Throws_when_message_has_no_attachments()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = repo.Upsert(Helpers.Sample("none@x"), "INBOX", "INBOX/cur", "none.eml", DateTimeOffset.UtcNow);

        var ex = Should.Throw<McpException>(() => Build(db).GetAttachmentText(partIndex: 0, id: id));
        ex.Message.ShouldContain("no attachments");
    }

    [Fact]
    public void Throws_when_part_index_not_present()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "one@x", "text", "done");

        var ex = Should.Throw<McpException>(() => Build(db).GetAttachmentText(partIndex: 5, id: id));
        ex.Message.ShouldContain("partIndex 5");
    }

    [Fact]
    public void Done_status_returns_summary_then_extracted_text()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "done@x", "Invoice total: $1,234.56\nDue 2026-07-15", "done");

        var result = Build(db).GetAttachmentText(partIndex: 0, id: id);

        result.Content.Count.ShouldBe(2);
        var summary = result.Content[0].ShouldBeOfType<TextContentBlock>();
        summary.Text.ShouldContain("report.pdf");
        summary.Text.ShouldContain("partIndex 0");

        var body = result.Content[1].ShouldBeOfType<TextContentBlock>();
        body.Text.ShouldContain("Invoice total: $1,234.56");
    }

    [Fact]
    public void Ocr_status_returns_recovered_text_like_done()
    {
        // OCR-recovered text is searchable (FTS + vectors include it) and
        // get_email reports it IndexedForSearch — this tool must serve it too.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "ocr@x", "Total amount due: $42.00", "ocr", fileName: "scan.pdf");

        var result = Build(db).GetAttachmentText(partIndex: 0, id: id);

        result.Content.Count.ShouldBe(2);
        var summary = result.Content[0].ShouldBeOfType<TextContentBlock>();
        summary.Text.ShouldContain("scan.pdf");
        summary.Text.ShouldContain("OCR");
        result.Content[1].ShouldBeOfType<TextContentBlock>().Text.ShouldContain("Total amount due: $42.00");
    }

    [Fact]
    public void Ocr_status_with_empty_text_reports_blank_scan()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "blankocr@x", text: "", status: "ocr", fileName: "blank.pdf");

        var result = Build(db).GetAttachmentText(partIndex: 0, id: id);

        result.Content.Count.ShouldBe(1);
        var msg = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        msg.ShouldContain("blank.pdf");
        msg.ShouldContain("no text was recovered");
    }

    [Fact]
    public void Lookup_by_messageId_works_identically()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        Seed(repo, "bymid@x", "hello from the pdf", "done");

        var result = Build(db).GetAttachmentText(partIndex: 0, messageId: "bymid@x");

        result.Content[1].ShouldBeOfType<TextContentBlock>().Text.ShouldContain("hello from the pdf");
    }

    [Fact]
    public void No_text_status_explains_scanned_pdf_and_points_at_page_image()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "scan@x", text: null, status: "no_text", fileName: "scan.pdf");

        var result = Build(db).GetAttachmentText(partIndex: 0, id: id);

        result.Content.Count.ShouldBe(1);
        var msg = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        msg.ShouldContain("scan.pdf");
        msg.ShouldContain("get_attachment_page_image");
        msg.ShouldContain("scanned");
    }

    [Fact]
    public void Encrypted_status_is_reported()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "enc@x", text: null, status: "encrypted");

        var msg = Build(db).GetAttachmentText(partIndex: 0, id: id).Content[0].ShouldBeOfType<TextContentBlock>().Text;
        msg.ShouldContain("encrypted");
    }

    [Fact]
    public void Null_status_reports_no_extraction_record()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "legacy@x", text: null, status: null);

        var msg = Build(db).GetAttachmentText(partIndex: 0, id: id).Content[0].ShouldBeOfType<TextContentBlock>().Text;
        msg.ShouldContain("no extraction record");
        msg.ShouldContain("view_attachment");
    }

    // --- maxChars / offset windowing ---

    private static string HundredChars() =>
        string.Concat(Enumerable.Range(0, 10).Select(i => $"chunk-{i:D2}: ")); // 10 × "chunk-NN: " = 100 chars

    [Fact]
    public void MaxChars_truncates_and_header_gives_total_and_next_offset()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var text = HundredChars();
        long id = Seed(repo, "win@x", text, "done");

        var result = Build(db).GetAttachmentText(partIndex: 0, id: id, maxChars: 40);

        result.Content.Count.ShouldBe(2);
        var header = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        header.ShouldContain("chars 0–40 of 100");
        header.ShouldContain("offset=40");
        result.Content[1].ShouldBeOfType<TextContentBlock>().Text.ShouldBe(text[..40]);
    }

    [Fact]
    public void Offset_pages_through_and_final_chunk_is_labelled()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var text = HundredChars();
        long id = Seed(repo, "page@x", text, "done");

        var middle = Build(db).GetAttachmentText(partIndex: 0, id: id, maxChars: 40, offset: 40);
        middle.Content[0].ShouldBeOfType<TextContentBlock>().Text.ShouldContain("chars 40–80 of 100");
        middle.Content[1].ShouldBeOfType<TextContentBlock>().Text.ShouldBe(text[40..80]);

        var last = Build(db).GetAttachmentText(partIndex: 0, id: id, maxChars: 40, offset: 80);
        var lastHeader = last.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        lastHeader.ShouldContain("chars 80–100 of 100");
        lastHeader.ShouldContain("final chunk");
        last.Content[1].ShouldBeOfType<TextContentBlock>().Text.ShouldBe(text[80..]);
    }

    [Fact]
    public void Offset_past_end_reports_total_length()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "past@x", HundredChars(), "done");

        var result = Build(db).GetAttachmentText(partIndex: 0, id: id, offset: 500);

        result.Content.Count.ShouldBe(1);
        var msg = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        msg.ShouldContain("past the end");
        msg.ShouldContain("100");
    }

    [Fact]
    public void Full_text_under_default_cap_is_returned_without_paging_chatter()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "small@x", "short doc", "done");

        var result = Build(db).GetAttachmentText(partIndex: 0, id: id);

        var header = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        header.ShouldNotContain("offset=");
        header.ShouldNotContain("chunk");
        result.Content[1].ShouldBeOfType<TextContentBlock>().Text.ShouldBe("short doc");
    }

    [Fact]
    public void Window_never_splits_a_surrogate_pair()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var text = "ab💡cd"; // 💡 is a surrogate pair at UTF-16 indexes 2–3
        long id = Seed(repo, "emoji@x", text, "done");

        // maxChars=3 would cut between the high and low surrogate; the window
        // must shrink to 2 so the slice stays valid UTF-16.
        var result = Build(db).GetAttachmentText(partIndex: 0, id: id, maxChars: 3);
        var slice = result.Content[1].ShouldBeOfType<TextContentBlock>().Text;
        slice.ShouldBe("ab");

        // Paging from inside the pair snaps back to include the whole pair.
        var next = Build(db).GetAttachmentText(partIndex: 0, id: id, maxChars: 10, offset: 3);
        next.Content[1].ShouldBeOfType<TextContentBlock>().Text.ShouldBe("💡cd");
    }

    [Fact]
    public void Invalid_offset_and_maxChars_are_rejected()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "bad@x", "text", "done");

        Should.Throw<McpException>(() => Build(db).GetAttachmentText(partIndex: 0, id: id, offset: -1));
        Should.Throw<McpException>(() => Build(db).GetAttachmentText(partIndex: 0, id: id, maxChars: 0));
    }
}
