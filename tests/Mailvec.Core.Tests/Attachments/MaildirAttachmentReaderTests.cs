using System.Text;
using Mailvec.Core.Attachments;
using Mailvec.Core.Models;
using MimeKit;
using IngestOptions = Mailvec.Core.Options.IngestOptions;

namespace Mailvec.Core.Tests.Attachments;

public class MaildirAttachmentReaderTests : IDisposable
{
    private readonly string _root;
    private readonly string _maildirRoot;

    public MaildirAttachmentReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mailvec-reader-tests-" + Guid.NewGuid().ToString("N"));
        _maildirRoot = Path.Combine(_root, "Mail");
        Directory.CreateDirectory(Path.Combine(_maildirRoot, "INBOX", "cur"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private MaildirAttachmentReader Reader() =>
        new(Microsoft.Extensions.Options.Options.Create(new IngestOptions { MaildirRoot = _maildirRoot }));

    private Message Stage(string fileName, long id = 1)
    {
        File.WriteAllText(Path.Combine(_maildirRoot, "INBOX", "cur", fileName), Eml);
        return new Message
        {
            Id = id,
            MessageId = $"m{id}@example.com",
            MaildirPath = "INBOX/cur",
            MaildirFilename = fileName,
            Folder = "INBOX",
            HasAttachments = true,
        };
    }

    private const string Eml = """
        Message-ID: <reader@example.com>
        From: a@example.com
        To: b@example.com
        Subject: s
        MIME-Version: 1.0
        Content-Type: multipart/mixed; boundary="outer"

        --outer
        Content-Type: text/plain; charset=utf-8

        body text
        --outer
        Content-Type: text/plain; name="note.txt"
        Content-Disposition: attachment; filename="note.txt"

        HELLO-BYTES
        --outer--
        """;

    [Fact]
    public void ReadBytes_returns_the_decoded_attachment_payload()
    {
        var bytes = Reader().ReadBytes(Stage("1.eml"), partIndex: 0);
        Encoding.UTF8.GetString(bytes).Trim().ShouldBe("HELLO-BYTES");
    }

    [Fact]
    public void Read_exposes_entity_metadata_alongside_bytes()
    {
        var data = Reader().Read(Stage("2.eml"), partIndex: 0);
        data.Bytes.Length.ShouldBeGreaterThan(0);
        ((MimeKit.MimePart)data.Entity).FileName.ShouldBe("note.txt");
    }

    [Fact]
    public void Throws_FileNotFound_when_the_eml_is_missing()
    {
        var ghost = new Message
        {
            Id = 9, MessageId = "ghost@x", MaildirPath = "INBOX/cur",
            MaildirFilename = "nope.eml", Folder = "INBOX", HasAttachments = true,
        };
        var ex = Should.Throw<FileNotFoundException>(() => Reader().ReadBytes(ghost, 0));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public void Throws_out_of_range_for_an_invalid_part_index()
    {
        var ex = Should.Throw<ArgumentOutOfRangeException>(() => Reader().ReadBytes(Stage("3.eml"), 5));
        ex.Message.ShouldContain("out of range");
    }

    [Fact]
    public void Refuses_to_read_through_a_symlinked_directory_that_escapes_the_root()
    {
        // A secret dir OUTSIDE the Maildir root, reachable only via a symlink
        // planted inside it. The lexical containment check passes (the target
        // string is under the root), so only symlink resolution catches it.
        var secret = Path.Combine(_root, "secret");
        Directory.CreateDirectory(secret);
        File.WriteAllText(Path.Combine(secret, "outside.eml"), Eml);
        Directory.CreateSymbolicLink(Path.Combine(_maildirRoot, "escape"), secret);

        var msg = new Message
        {
            Id = 7, MessageId = "escape@x", MaildirPath = "escape",
            MaildirFilename = "outside.eml", Folder = "INBOX", HasAttachments = true,
        };

        var ex = Should.Throw<InvalidOperationException>(() => Reader().ReadBytes(msg, 0));
        ex.Message.ShouldContain("outside Maildir root");
    }

    // multipart/mixed [ multipart/related [ text/html, inline image/png ], attachment ].
    // The inline PNG (base64 "IMGDATA") is not in mime.Attachments — it's only
    // reachable via the shared MessageParts enumeration, at index 1 (after the
    // attachment at index 0).
    private const string EmlWithInlineImage = """
        Message-ID: <inline-reader@example.com>
        From: a@example.com
        To: b@example.com
        Subject: s
        MIME-Version: 1.0
        Content-Type: multipart/mixed; boundary="outer"

        --outer
        Content-Type: multipart/related; boundary="rel"

        --rel
        Content-Type: text/html; charset=utf-8

        <div><img src="cid:img1"></div>
        --rel
        Content-Type: image/png; name="inline.png"
        Content-Disposition: inline; filename="inline.png"
        Content-Transfer-Encoding: base64
        Content-ID: <img1>

        SU1HREFUQQ==
        --rel--
        --outer
        Content-Type: text/plain; name="note.txt"
        Content-Disposition: attachment; filename="note.txt"

        ATTACH-BYTES
        --outer--
        """;

    private Message StageInline(string fileName, long id = 1)
    {
        File.WriteAllText(Path.Combine(_maildirRoot, "INBOX", "cur", fileName), EmlWithInlineImage);
        return new Message
        {
            Id = id, MessageId = $"m{id}@example.com", MaildirPath = "INBOX/cur",
            MaildirFilename = fileName, Folder = "INBOX", HasAttachments = true,
        };
    }

    [Fact]
    public void Attachment_keeps_part_index_zero_when_an_inline_image_is_present()
    {
        // Existing rows must not shift: the real attachment stays at index 0.
        var data = Reader().Read(StageInline("inline0.eml"), partIndex: 0);
        ((MimePart)data.Entity).FileName.ShouldBe("note.txt");
        Encoding.UTF8.GetString(data.Bytes).Trim().ShouldBe("ATTACH-BYTES");
    }

    [Fact]
    public void Inline_image_is_readable_at_the_appended_part_index()
    {
        // part_index 1 (what the backfill assigns the inline image) round-trips to
        // the inline PNG's decoded bytes ("IMGDATA").
        var data = Reader().Read(StageInline("inline1.eml"), partIndex: 1);
        ((MimePart)data.Entity).ContentType.MediaType.ShouldBe("image");
        Encoding.UTF8.GetString(data.Bytes).ShouldBe("IMGDATA");
    }
}
