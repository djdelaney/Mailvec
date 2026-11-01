using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Mcp.Tools;
using ModelContextProtocol;

namespace Mailvec.Mcp.Tests.Tools;

public class GetEmailHeadersToolTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _maildirRoot;

    public GetEmailHeadersToolTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mailvec-mcp-headers-tests-" + Guid.NewGuid().ToString("N"));
        _maildirRoot = Path.Combine(_tempRoot, "Mail");
        Directory.CreateDirectory(Path.Combine(_maildirRoot, "INBOX", "cur"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    // Newest hop first, a folded Authentication-Results, a folded ARC seal and
    // a header repeated (Received): the shapes a deliverability check reads.
    private const string Headers =
        "Return-Path: <bounce@sender.example>\r\n" +
        "Received: from mx.sender.example (mx.sender.example [192.0.2.1])\r\n" +
        "\tby mx1.messagingengine.com; Sat, 1 Nov 2026 10:00:00 -0400\r\n" +
        "Authentication-Results: mx1.messagingengine.com;\r\n" +
        "    dkim=pass header.d=sender.example;\r\n" +
        "    dmarc=pass header.from=sender.example\r\n" +
        "ARC-Seal: i=1; a=rsa-sha256; cv=none;\r\n" +
        "    b=abc\r\n" +
        "Received: from internal.sender.example by mx.sender.example\r\n" +
        "Message-ID: <hdr@example.com>\r\n" +
        "Subject: Your invoice\r\n";

    private GetEmailHeadersTool Build(TempDatabase db) => new(
        new MessageRepository(db.Connections),
        new MaildirAttachmentReader(Microsoft.Extensions.Options.Options.Create(new IngestOptions { MaildirRoot = _maildirRoot })),
        Helpers.NoopLogger());

    private long Stage(TempDatabase db, string content, string messageId = "hdr@example.com", string file = "1.eml")
    {
        File.WriteAllText(Path.Combine(_maildirRoot, "INBOX", "cur", file), content);
        return new MessageRepository(db.Connections)
            .Upsert(Helpers.Sample(messageId), "INBOX", "INBOX/cur", file, DateTimeOffset.UtcNow).Id;
    }

    [Fact]
    public void Returns_the_header_block_verbatim_and_stops_at_the_blank_line()
    {
        using var db = new TempDatabase();
        var id = Stage(db, Headers + "\r\nBody: not a header\r\nmore body\r\n");

        var r = Build(db).GetEmailHeaders(id: id);

        // Order, folding and CRLF untouched; the body's look-alike line excluded.
        r.RawHeaders.ShouldBe(Headers);
        r.TotalFieldCount.ShouldBe(7);
        r.FieldCount.ShouldBe(7);
        r.Truncated.ShouldBeFalse();
        r.MessageId.ShouldBe("hdr@example.com");
        r.Folder.ShouldBe("INBOX");
    }

    [Fact]
    public void Reads_the_file_not_the_raw_headers_column()
    {
        // messages.raw_headers held "MimeKit.HeaderList" for every row ever
        // indexed; the fixture stores a one-line stand-in. Neither may leak.
        using var db = new TempDatabase();
        Stage(db, Headers + "\r\nbody\r\n");

        var r = Build(db).GetEmailHeaders(messageId: "hdr@example.com");

        r.RawHeaders.ShouldContain("Authentication-Results: mx1.messagingengine.com;");
        r.RawHeaders.ShouldNotContain("MimeKit.HeaderList");
    }

    [Fact]
    public void Names_select_fields_with_their_continuation_lines_in_original_order()
    {
        using var db = new TempDatabase();
        var id = Stage(db, Headers + "\r\nbody\r\n");

        var r = Build(db).GetEmailHeaders(id: id, names: ["received", "Authentication-Results:", "arc-*"]);

        r.RawHeaders.ShouldBe(
            "Received: from mx.sender.example (mx.sender.example [192.0.2.1])\r\n" +
            "\tby mx1.messagingengine.com; Sat, 1 Nov 2026 10:00:00 -0400\r\n" +
            "Authentication-Results: mx1.messagingengine.com;\r\n" +
            "    dkim=pass header.d=sender.example;\r\n" +
            "    dmarc=pass header.from=sender.example\r\n" +
            "ARC-Seal: i=1; a=rsa-sha256; cv=none;\r\n" +
            "    b=abc\r\n" +
            "Received: from internal.sender.example by mx.sender.example\r\n");
        r.FieldCount.ShouldBe(4);
        r.TotalFieldCount.ShouldBe(7);
    }

    [Fact]
    public void Blank_or_unmatched_names_behave_sensibly()
    {
        using var db = new TempDatabase();
        var id = Stage(db, Headers + "\r\nbody\r\n");
        var tool = Build(db);

        tool.GetEmailHeaders(id: id, names: ["", "  "]).RawHeaders.ShouldBe(Headers); // no filter
        var none = tool.GetEmailHeaders(id: id, names: ["X-Not-There"]);
        none.RawHeaders.ShouldBeEmpty();
        none.FieldCount.ShouldBe(0);
        none.TotalFieldCount.ShouldBe(7);
    }

    [Fact]
    public void Lf_only_files_and_files_with_no_body_are_read_whole()
    {
        using var db = new TempDatabase();
        var lf = "From: a@example.com\nSubject: lf\n";
        var tool = Build(db);

        var withBody = Stage(db, lf + "\nbody\n", "lf@example.com", "lf.eml");
        tool.GetEmailHeaders(id: withBody).RawHeaders.ShouldBe(lf);

        // No blank line at all: the whole file is headers, last line unterminated.
        var noBody = Stage(db, "From: a@example.com\nSubject: none", "nobody@example.com", "nobody.eml");
        var r = tool.GetEmailHeaders(id: noBody);
        r.RawHeaders.ShouldBe("From: a@example.com\nSubject: none");
        r.Truncated.ShouldBeFalse();
        r.TotalFieldCount.ShouldBe(2);
    }

    [Fact]
    public void An_oversized_header_block_is_cut_at_the_last_whole_line()
    {
        // The block's length is sender-chosen; the read is bounded and never
        // hands back half a header as if it were whole.
        using var db = new TempDatabase();
        var line = "X-Filler: " + new string('a', 1000) + "\r\n";
        var lines = GetEmailHeadersTool.HeaderBlockMaxBytes / line.Length + 10;
        var id = Stage(db, string.Concat(Enumerable.Repeat(line, lines)) + "\r\nbody\r\n");

        var r = Build(db).GetEmailHeaders(id: id);

        r.Truncated.ShouldBeTrue();
        r.RawHeaders.Length.ShouldBeLessThanOrEqualTo(GetEmailHeadersTool.HeaderBlockMaxBytes);
        r.RawHeaders.ShouldEndWith("\r\n");
        r.RawHeaders.Length.ShouldBe(r.TotalFieldCount * line.Length);
    }

    [Fact]
    public void A_missing_source_file_is_reported_without_the_path()
    {
        using var db = new TempDatabase();
        var id = Stage(db, Headers + "\r\nbody\r\n");
        File.Delete(Path.Combine(_maildirRoot, "INBOX", "cur", "1.eml"));

        var ex = Should.Throw<McpException>(() => Build(db).GetEmailHeaders(id: id));

        ex.Message.ShouldContain("no longer available");
        ex.Message.ShouldNotContain(_maildirRoot);
    }

    [Fact]
    public void Identifier_rules_match_get_email()
    {
        using var db = new TempDatabase();
        var id = Stage(db, Headers + "\r\nbody\r\n");
        var tool = Build(db);

        Should.Throw<McpException>(() => tool.GetEmailHeaders()).Message.ShouldContain("Provide either");
        Should.Throw<McpException>(() => tool.GetEmailHeaders(id: id, messageId: "hdr@example.com")).Message.ShouldContain("not both");
        Should.Throw<McpException>(() => tool.GetEmailHeaders(id: 999)).Message.ShouldContain("No message with id 999");
    }
}
