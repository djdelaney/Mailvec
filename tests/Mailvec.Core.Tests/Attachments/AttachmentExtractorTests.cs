using Mailvec.Core.Attachments;
using Mailvec.Core.Models;
using McpOptions = Mailvec.Core.Options.McpOptions;
using IngestOptions = Mailvec.Core.Options.IngestOptions;

namespace Mailvec.Core.Tests.Attachments;

public class AttachmentExtractorTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _maildirRoot;
    private readonly string _downloadDir;

    public AttachmentExtractorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mailvec-attach-tests-" + Guid.NewGuid().ToString("N"));
        _maildirRoot = Path.Combine(_tempRoot, "Mail");
        _downloadDir = Path.Combine(_tempRoot, "downloads");
        Directory.CreateDirectory(Path.Combine(_maildirRoot, "INBOX", "cur"));
        Directory.CreateDirectory(_downloadDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private AttachmentExtractor BuildExtractor(int inlineTextMax = 256 * 1024)
    {
        var ingest = Microsoft.Extensions.Options.Options.Create(new IngestOptions { MaildirRoot = _maildirRoot });
        var mcp = Microsoft.Extensions.Options.Options.Create(new McpOptions
        {
            AttachmentDownloadDir = _downloadDir,
            AttachmentInlineTextMaxBytes = inlineTextMax,
        });
        return new AttachmentExtractor(ingest, mcp);
    }

    /// <summary>
    /// Stage a Maildir EML and return a Message instance pointing at it. Mirrors
    /// the path layout the indexer's MaildirScanner produces (relative
    /// "INBOX/cur" + leaf filename), so the extractor's path resolution matches
    /// the production code path.
    /// </summary>
    private Message StageEml(string fileName, string emlBody, long messageId = 1, bool hasAttachments = true)
    {
        var path = Path.Combine(_maildirRoot, "INBOX", "cur", fileName);
        File.WriteAllText(path, emlBody);
        return new Message
        {
            Id = messageId,
            MessageId = $"msg-{messageId}@example.com",
            MaildirPath = "INBOX/cur",
            MaildirFilename = fileName,
            Folder = "INBOX",
            HasAttachments = hasAttachments,
        };
    }

    private const string PdfMessage = """
        Message-ID: <attach-005@example.com>
        From: carol@example.com
        To: alice@example.com
        Subject: Quote attached
        MIME-Version: 1.0
        Content-Type: multipart/mixed; boundary="outer"

        --outer
        Content-Type: text/plain; charset=utf-8

        See attached.
        --outer
        Content-Type: application/pdf; name="quote.pdf"
        Content-Disposition: attachment; filename="quote.pdf"
        Content-Transfer-Encoding: base64

        JVBERi0xLjAKJSVFT0YK
        --outer--
        """;

    private const string CsvMessage = """
        Message-ID: <csv-006@example.com>
        From: alice@example.com
        To: bob@example.com
        Subject: Sales numbers
        MIME-Version: 1.0
        Content-Type: multipart/mixed; boundary="b"

        --b
        Content-Type: text/plain

        Numbers attached.
        --b
        Content-Type: text/csv; name="sales.csv"
        Content-Disposition: attachment; filename="sales.csv"

        region,units
        north,42
        south,17
        --b--
        """;

    [Fact]
    public void Extracts_pdf_to_download_dir()
    {
        var ext = BuildExtractor();
        var msg = StageEml("100.imapfetch:2,S", PdfMessage, messageId: 100);

        var r = ext.Extract(msg, partIndex: 0);

        r.FileName.ShouldBe("quote.pdf");
        r.ContentType.ShouldBe("application/pdf");
        r.SizeBytes.ShouldBeGreaterThan(0);
        r.WasReused.ShouldBeFalse();
        r.InlineText.ShouldBeNull(); // PDF is not text-ish
        File.Exists(r.FilePath).ShouldBeTrue();
        // Path is inside the configured download dir, NOT the system Downloads.
        r.FilePath.ShouldStartWith(_downloadDir);
        // Filename includes message id + part index so collisions are impossible.
        Path.GetFileName(r.FilePath).ShouldBe("100-0-quote.pdf");
        // Bytes on disk start with the PDF magic header.
        var bytes = File.ReadAllBytes(r.FilePath);
        System.Text.Encoding.ASCII.GetString(bytes, 0, 5).ShouldBe("%PDF-");
    }

    [Fact]
    public void Re_extract_reuses_existing_file_when_size_matches()
    {
        var ext = BuildExtractor();
        var msg = StageEml("101.imapfetch:2,S", PdfMessage, messageId: 101);

        var first = ext.Extract(msg, partIndex: 0);
        first.WasReused.ShouldBeFalse();

        var second = ext.Extract(msg, partIndex: 0);
        second.WasReused.ShouldBeTrue();
        second.FilePath.ShouldBe(first.FilePath);
        // Bytes are identical (we didn't re-decode and rewrite).
        File.ReadAllBytes(first.FilePath).ShouldBe(File.ReadAllBytes(second.FilePath));
    }

    [Fact]
    public void Extracts_text_attachment_with_inline_text()
    {
        var ext = BuildExtractor();
        var msg = StageEml("102.imapfetch:2,S", CsvMessage, messageId: 102);

        var r = ext.Extract(msg, partIndex: 0);

        r.FileName.ShouldBe("sales.csv");
        r.ContentType.ShouldBe("text/csv");
        r.InlineText.ShouldNotBeNull().ShouldContain("region,units");
        File.Exists(r.FilePath).ShouldBeTrue();
    }

    [Fact]
    public void Inline_text_skipped_for_files_over_threshold()
    {
        var ext = BuildExtractor(inlineTextMax: 16); // tiny — CSV body exceeds this
        var msg = StageEml("103.imapfetch:2,S", CsvMessage, messageId: 103);

        var r = ext.Extract(msg, partIndex: 0);

        r.InlineText.ShouldBeNull();
        // The file is still saved to disk regardless.
        File.Exists(r.FilePath).ShouldBeTrue();
    }

    [Fact]
    public void Out_of_range_partIndex_throws()
    {
        var ext = BuildExtractor();
        var msg = StageEml("104.imapfetch:2,S", PdfMessage, messageId: 104);

        Should.Throw<ArgumentOutOfRangeException>(() => ext.Extract(msg, partIndex: 5));
    }

    [Fact]
    public void Missing_maildir_file_throws_FileNotFound()
    {
        var ext = BuildExtractor();
        var msg = new Message
        {
            Id = 200,
            MessageId = "ghost@example.com",
            MaildirPath = "INBOX/cur",
            MaildirFilename = "does-not-exist",
            Folder = "INBOX",
            HasAttachments = true,
        };

        Should.Throw<FileNotFoundException>(() => ext.Extract(msg, partIndex: 0));
    }

    [Fact]
    public void Generic_octet_stream_mime_resolves_from_pdf_extension()
    {
        // Many mailers attach PDFs with Content-Type: application/octet-stream
        // and rely on the .pdf filename for type info. We override the generic
        // MIME because a real MIME is what consumers (and Claude's image
        // detection branch) need.
        const string genericPdf = """
            Message-ID: <gen-008@example.com>
            From: x@example.com
            To: y@example.com
            Subject: invoice
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="m"

            --m
            Content-Type: text/plain

            See attached.
            --m
            Content-Type: application/octet-stream; name="invoice.pdf"
            Content-Disposition: attachment; filename="invoice.pdf"
            Content-Transfer-Encoding: base64

            JVBERi0xLjAKJSVFT0YK
            --m--
            """;
        var ext = BuildExtractor();
        var msg = StageEml("130.imapfetch:2,S", genericPdf, messageId: 130);

        var r = ext.Extract(msg, partIndex: 0);

        r.ContentType.ShouldBe("application/pdf");
    }

    [Fact]
    public void Specific_mime_is_preserved_even_when_extension_disagrees()
    {
        const string declaredText = """
            Message-ID: <gen-009@example.com>
            From: x@example.com
            To: y@example.com
            Subject: report
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="m"

            --m
            Content-Type: text/plain

            See attached.
            --m
            Content-Type: text/plain; name="report.pdf"
            Content-Disposition: attachment; filename="report.pdf"

            this is actually a text file
            --m--
            """;
        var ext = BuildExtractor();
        var msg = StageEml("131.imapfetch:2,S", declaredText, messageId: 131);

        var r = ext.Extract(msg, partIndex: 0);

        r.ContentType.ShouldBe("text/plain");
    }

    [Fact]
    public void Attachment_with_traversal_filename_lands_inside_download_dir()
    {
        // A malicious sender could name the attachment "../../etc/passwd".
        // ResolveSafeFileName strips path components, so the saved file
        // ends up safely under the download dir, not /etc.
        const string nasty = """
            Message-ID: <bad-007@example.com>
            From: x@example.com
            To: y@example.com
            Subject: bad filename
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="z"

            --z
            Content-Type: text/plain

            See attached.
            --z
            Content-Type: application/octet-stream; name="../../etc/passwd"
            Content-Disposition: attachment; filename="../../etc/passwd"

            content
            --z--
            """;
        var ext = BuildExtractor();
        var msg = StageEml("105.imapfetch:2,S", nasty, messageId: 105);

        var r = ext.Extract(msg, partIndex: 0);

        r.FileName.ShouldBe("passwd");
        r.FilePath.ShouldStartWith(_downloadDir);
        // Saved filename: messageId-partIndex-sanitized-name
        Path.GetFileName(r.FilePath).ShouldBe("105-0-passwd");
        File.Exists(r.FilePath).ShouldBeTrue();
    }
}
