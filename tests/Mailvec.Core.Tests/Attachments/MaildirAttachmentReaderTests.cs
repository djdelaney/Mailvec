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
        var bytes = Reader().ReadBytes(Stage("1.eml"), partIndex: 0, maxBytes: null);
        Encoding.UTF8.GetString(bytes).Trim().ShouldBe("HELLO-BYTES");
    }

    [Fact]
    public void A_part_over_the_ceiling_is_refused_rather_than_decoded()
    {
        // "HELLO-BYTES" plus its trailing newline is 12 bytes.
        var ex = Should.Throw<AttachmentTooLargeException>(
            () => Reader().ReadBytes(Stage("cap.eml"), partIndex: 0, maxBytes: 4));

        ex.LimitBytes.ShouldBe(4);
        // Names the attachment so the caller can say WHICH one was refused...
        ex.Message.ShouldContain("note.txt");
        // ...but not the archive layout: the MCP tools surface this text to a
        // remote client, same as the FileNotFoundException below.
        ex.Message.ShouldNotContain(_maildirRoot);
    }

    [Fact]
    public void A_part_exactly_at_the_ceiling_is_allowed()
    {
        // The boundary is inclusive — a cap is the largest allowed size, not
        // the smallest refused one, and an off-by-one here would refuse a
        // document that fits.
        var bytes = Reader().ReadBytes(Stage("exact.eml"), partIndex: 0, maxBytes: 12);
        Encoding.UTF8.GetString(bytes).Trim().ShouldBe("HELLO-BYTES");
    }

    [Fact]
    public void EnsureSourceExists_throws_the_same_answer_as_a_read_without_decoding()
    {
        // view_attachment skips the read for types it can summarise from
        // metadata; this is how it keeps the "source has vanished" answer.
        var msg = Stage("gone.eml");
        Reader().EnsureSourceExists(msg);   // present: no throw

        File.Delete(Path.Combine(_maildirRoot, "INBOX", "cur", "gone.eml"));

        var ex = Should.Throw<FileNotFoundException>(() => Reader().EnsureSourceExists(msg));
        ex.Message.ShouldContain("no longer available");
        ex.Message.ShouldNotContain(_maildirRoot);
    }

    [Fact]
    public void Read_exposes_entity_metadata_alongside_bytes()
    {
        var data = Reader().Read(Stage("2.eml"), partIndex: 0, maxBytes: null);
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
        var ex = Should.Throw<FileNotFoundException>(() => Reader().ReadBytes(ghost, 0, maxBytes: null));

        // The message is sanitized because both MCP attachment tools surface it
        // verbatim to the remote client; the path rides on FileName instead, for
        // the on-box consumers (CLI backfill, embedder OCR) that need to know
        // WHICH file is missing. Assert both halves — a future change that
        // folded the path back into the message would otherwise pass.
        ex.Message.ShouldContain("no longer available");
        ex.Message.ShouldContain("9", Case.Insensitive);
        ex.Message.ShouldNotContain("nope.eml");
        ex.Message.ShouldNotContain("ghost@x", Case.Insensitive);
        ex.FileName.ShouldNotBeNull().ShouldContain("nope.eml");
    }

    [Fact]
    public void Throws_out_of_range_for_an_invalid_part_index()
    {
        var ex = Should.Throw<ArgumentOutOfRangeException>(() => Reader().ReadBytes(Stage("3.eml"), 5, maxBytes: null));
        ex.Message.ShouldContain("out of range");
    }

    // ── The symlink probe fails CLOSED ───────────────────────────────────────

    [Fact]
    public void A_regular_file_is_reported_as_not_a_link()
    {
        // The normal path must stay silent — this guard runs on every
        // attachment read, so a probe that threw on ordinary files would break
        // view_attachment and the OCR pass outright.
        var file = Path.Combine(_maildirRoot, "plain.txt");
        File.WriteAllText(file, "hi");

        MaildirAttachmentReader.LinkTargetOf(file).ShouldBeNull();
        MaildirAttachmentReader.LinkTargetOf(_maildirRoot).ShouldBeNull();
    }

    [Fact]
    public void A_symlink_reports_its_target()
    {
        var victim = Path.Combine(_root, "outside.txt");
        File.WriteAllText(victim, "secret");
        var link = Path.Combine(_maildirRoot, "link.txt");
        File.CreateSymbolicLink(link, victim);

        MaildirAttachmentReader.LinkTargetOf(link).ShouldNotBeNull();
    }

    [Fact]
    public void An_undeterminable_path_throws_rather_than_reporting_not_a_link()
    {
        // This used to `catch { return null; }` — i.e. "not a link" — which
        // fails OPEN in a containment guard: an erroring path silently skipped
        // symlink resolution, leaving only the lexical check, which by
        // construction cannot catch a symlinked component escaping the root.
        // "Couldn't tell" must not resolve to "safe".
        var undeterminable = Path.Combine(_maildirRoot, "bad\0name");

        Should.Throw<InvalidOperationException>(() => MaildirAttachmentReader.LinkTargetOf(undeterminable))
            .Message.ShouldContain("Refusing the read");
    }

    [Fact]
    public void The_resolved_path_is_what_gets_opened_not_the_lexical_one()
    {
        // The guard resolves symlinks to decide, then the caller opens whatever
        // the resolve returned. It used to return the LEXICAL path — checking
        // one path and opening another, which is the window an actor with write
        // access to the Maildir could use to swap a directory component for an
        // escaping symlink between the two. It returns the resolved path now.
        //
        // Observable proof: an in-root symlinked directory. Reading through it
        // works either way (same bytes), so assert on the path the reader
        // reports — FileNotFoundException.FileName carries it, and it must be
        // the physical location, not the route taken to it.
        var real = Path.Combine(_maildirRoot, "Real", "cur");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(Path.Combine(_maildirRoot, "Alias"), Path.Combine(_maildirRoot, "Real"));

        // Present through the alias: the read succeeds (in-root, so allowed).
        File.WriteAllText(Path.Combine(real, "there.eml"), Eml);
        var present = new Message
        {
            Id = 20, MessageId = "alias@x", MaildirPath = "Alias/cur",
            MaildirFilename = "there.eml", Folder = "INBOX", HasAttachments = true,
        };
        Encoding.UTF8.GetString(Reader().ReadBytes(present, 0, maxBytes: null)).ShouldContain("HELLO-BYTES");

        // Absent through the alias: the reported path went through the symlink.
        var missing = new Message
        {
            Id = 21, MessageId = "alias-gone@x", MaildirPath = "Alias/cur",
            MaildirFilename = "gone.eml", Folder = "INBOX", HasAttachments = true,
        };
        var ex = Should.Throw<FileNotFoundException>(() => Reader().ReadBytes(missing, 0, maxBytes: null));
        ex.FileName.ShouldNotBeNull();
        ex.FileName!.ShouldContain($"Real{Path.DirectorySeparatorChar}cur");
        ex.FileName.ShouldNotContain("Alias");
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

        var ex = Should.Throw<InvalidOperationException>(() => Reader().ReadBytes(msg, 0, maxBytes: null));
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
        var data = Reader().Read(StageInline("inline0.eml"), partIndex: 0, maxBytes: null);
        ((MimePart)data.Entity).FileName.ShouldBe("note.txt");
        Encoding.UTF8.GetString(data.Bytes).Trim().ShouldBe("ATTACH-BYTES");
    }

    [Fact]
    public void Inline_image_is_readable_at_the_appended_part_index()
    {
        // part_index 1 (what the backfill assigns the inline image) round-trips to
        // the inline PNG's decoded bytes ("IMGDATA").
        var data = Reader().Read(StageInline("inline1.eml"), partIndex: 1, maxBytes: null);
        ((MimePart)data.Entity).ContentType.MediaType.ShouldBe("image");
        Encoding.UTF8.GetString(data.Bytes).ShouldBe("IMGDATA");
    }
}
