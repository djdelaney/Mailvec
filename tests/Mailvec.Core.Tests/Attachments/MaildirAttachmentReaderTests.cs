using System.Text;
using Mailvec.Core.Attachments;
using Mailvec.Core.Models;
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
}
