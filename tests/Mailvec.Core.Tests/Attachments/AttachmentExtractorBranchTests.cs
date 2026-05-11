using System.Text;
using Mailvec.Core.Attachments;
using Mailvec.Core.Models;
using McpOptions = Mailvec.Core.Options.McpOptions;
using IngestOptions = Mailvec.Core.Options.IngestOptions;

namespace Mailvec.Core.Tests.Attachments;

/// <summary>
/// Branch-coverage tests for <see cref="AttachmentExtractor"/> — exercises the
/// MIME-from-extension table, the synthesized-filename path when the part has
/// no Content-Disposition name, the symlink-rejection guard in
/// <c>ResolveSafeOutputPath</c>, the json/xml inline-text classifications, and
/// the UTF-8 decode-failure catch.
/// </summary>
public class AttachmentExtractorBranchTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _maildirRoot;
    private readonly string _downloadDir;

    public AttachmentExtractorBranchTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mailvec-attach-branch-" + Guid.NewGuid().ToString("N"));
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

    private Message StageEml(string fileName, string emlBody, long messageId)
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
            HasAttachments = true,
        };
    }

    private static string BuildGenericOctetEml(string filename, string base64Body) => $"""
        Message-ID: <test@example.com>
        From: x@example.com
        To: y@example.com
        Subject: test
        MIME-Version: 1.0
        Content-Type: multipart/mixed; boundary="m"

        --m
        Content-Type: text/plain

        See attached.
        --m
        Content-Type: application/octet-stream; name="{filename}"
        Content-Disposition: attachment; filename="{filename}"
        Content-Transfer-Encoding: base64

        {base64Body}
        --m--
        """;

    [Theory]
    [InlineData("photo.png", "image/png")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("animation.gif", "image/gif")]
    [InlineData("image.webp", "image/webp")]
    [InlineData("vector.svg", "image/svg+xml")]
    [InlineData("snap.heic", "image/heic")]
    [InlineData("scan.tiff", "image/tiff")]
    [InlineData("scan.tif", "image/tiff")]
    [InlineData("bitmap.bmp", "image/bmp")]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("data.csv", "text/csv")]
    [InlineData("page.html", "text/html")]
    [InlineData("page.htm", "text/html")]
    [InlineData("payload.xml", "application/xml")]
    [InlineData("payload.json", "application/json")]
    [InlineData("config.yaml", "application/yaml")]
    [InlineData("config.yml", "application/yaml")]
    [InlineData("readme.md", "text/markdown")]
    [InlineData("bundle.zip", "application/zip")]
    [InlineData("report.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("sheet.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("deck.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    [InlineData("report.doc", "application/msword")]
    [InlineData("sheet.xls", "application/vnd.ms-excel")]
    [InlineData("deck.ppt", "application/vnd.ms-powerpoint")]
    [InlineData("tune.mp3", "audio/mpeg")]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("clip.mov", "video/quicktime")]
    [InlineData("sample.wav", "audio/wav")]
    public void Generic_octet_stream_is_promoted_by_filename_extension(string filename, string expectedMime)
    {
        var ext = BuildExtractor();
        var msg = StageEml($"110-{filename}.imapfetch:2,S",
            BuildGenericOctetEml(filename, "QUJD"),  // "ABC" base64
            messageId: 110);

        var r = ext.Extract(msg, partIndex: 0);

        r.ContentType.ShouldBe(expectedMime);
    }

    [Fact]
    public void Unknown_extension_keeps_generic_octet_stream()
    {
        var ext = BuildExtractor();
        var msg = StageEml("111.imapfetch:2,S",
            BuildGenericOctetEml("payload.weirdext", "QUJD"),
            messageId: 111);

        var r = ext.Extract(msg, partIndex: 0);

        r.ContentType.ShouldBe("application/octet-stream");
    }

    [Fact]
    public void Binary_octet_stream_is_also_treated_as_generic()
    {
        // `binary/octet-stream` is a non-standard but seen-in-the-wild variant.
        var eml = """
            Message-ID: <bin@example.com>
            From: x@example.com
            To: y@example.com
            Subject: t
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="m"

            --m
            Content-Type: text/plain

            x
            --m
            Content-Type: binary/octet-stream; name="report.pdf"
            Content-Disposition: attachment; filename="report.pdf"
            Content-Transfer-Encoding: base64

            JVBERi0xLjAKJSVFT0YK
            --m--
            """;
        var ext = BuildExtractor();
        var msg = StageEml("112.imapfetch:2,S", eml, messageId: 112);

        var r = ext.Extract(msg, partIndex: 0);

        r.ContentType.ShouldBe("application/pdf");
    }

    [Fact]
    public void Missing_filename_falls_back_to_synthesized_name_with_extension()
    {
        // No Content-Disposition filename, no Content-Type name — extractor
        // should synthesize "attachment-{partIndex}.{ext}" from the MIME type.
        var eml = """
            Message-ID: <nofn@example.com>
            From: x@example.com
            To: y@example.com
            Subject: no filename
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="m"

            --m
            Content-Type: text/plain

            see below
            --m
            Content-Type: application/pdf
            Content-Disposition: attachment
            Content-Transfer-Encoding: base64

            JVBERi0xLjAKJSVFT0YK
            --m--
            """;
        var ext = BuildExtractor();
        var msg = StageEml("113.imapfetch:2,S", eml, messageId: 113);

        var r = ext.Extract(msg, partIndex: 0);

        r.FileName.ShouldBe("attachment-0.pdf");
        Path.GetFileName(r.FilePath).ShouldBe("113-0-attachment-0.pdf");
    }

    [Fact]
    public void Missing_filename_with_unknown_mime_synthesizes_extensionless_name()
    {
        // ExtensionFromContentType returns string.Empty for unknown MIMEs.
        var eml = """
            Message-ID: <nofn2@example.com>
            From: x@example.com
            To: y@example.com
            Subject: no filename, weird mime
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="m"

            --m
            Content-Type: text/plain

            see below
            --m
            Content-Type: application/x-custom-thing
            Content-Disposition: attachment
            Content-Transfer-Encoding: base64

            QUJD
            --m--
            """;
        var ext = BuildExtractor();
        var msg = StageEml("114.imapfetch:2,S", eml, messageId: 114);

        var r = ext.Extract(msg, partIndex: 0);

        r.FileName.ShouldBe("attachment-0");
    }

    [Fact]
    public void Json_attachment_is_classified_as_inline_text()
    {
        // application/json is in InlineTextContentTypes but isn't text/* —
        // exercises the non-text/* branch of IsTextLikeContentType.
        var eml = """
            Message-ID: <json@example.com>
            From: x@example.com
            To: y@example.com
            Subject: json
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="m"

            --m
            Content-Type: text/plain

            attached
            --m
            Content-Type: application/json; name="data.json"
            Content-Disposition: attachment; filename="data.json"

            {"hello": "world"}
            --m--
            """;
        var ext = BuildExtractor();
        var msg = StageEml("115.imapfetch:2,S", eml, messageId: 115);

        var r = ext.Extract(msg, partIndex: 0);

        r.ContentType.ShouldBe("application/json");
        r.InlineText.ShouldNotBeNull().ShouldContain("\"hello\": \"world\"");
    }

    [Fact]
    public void Inline_text_disabled_when_max_bytes_is_zero()
    {
        // Early-return branch in TryDecodeInlineText.
        var eml = """
            Message-ID: <csv0@example.com>
            From: x@example.com
            To: y@example.com
            Subject: t
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="m"

            --m
            Content-Type: text/plain

            x
            --m
            Content-Type: text/csv; name="sales.csv"
            Content-Disposition: attachment; filename="sales.csv"

            a,b
            1,2
            --m--
            """;
        var ext = BuildExtractor(inlineTextMax: 0);
        var msg = StageEml("116.imapfetch:2,S", eml, messageId: 116);

        var r = ext.Extract(msg, partIndex: 0);

        r.InlineText.ShouldBeNull();
        File.Exists(r.FilePath).ShouldBeTrue();
    }

    [Fact]
    public void Inline_text_skipped_for_binary_content_type()
    {
        // image/png is not text-ish, so even small images don't get inline text.
        var ext = BuildExtractor();
        var msg = StageEml("117.imapfetch:2,S",
            BuildGenericOctetEml("tiny.png", Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 })),
            messageId: 117);

        var r = ext.Extract(msg, partIndex: 0);

        r.ContentType.ShouldBe("image/png");
        r.InlineText.ShouldBeNull();
    }

    [Fact]
    public void Invalid_utf8_in_text_attachment_omits_inline_text_but_still_writes_file()
    {
        // Attachment claims text/csv but the bytes aren't valid UTF-8 — strict
        // UTF8Encoding throws DecoderFallbackException; we catch and return
        // null. The file still lands on disk regardless.
        var invalidBytes = new byte[] { 0x68, 0x69, 0xC3, 0x28, 0x21 }; // "hi" + invalid utf-8 + "!"
        var eml = $"""
            Message-ID: <badutf@example.com>
            From: x@example.com
            To: y@example.com
            Subject: bad utf8
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="m"

            --m
            Content-Type: text/plain

            x
            --m
            Content-Type: text/csv; name="bad.csv"
            Content-Disposition: attachment; filename="bad.csv"
            Content-Transfer-Encoding: base64

            {Convert.ToBase64String(invalidBytes)}
            --m--
            """;
        var ext = BuildExtractor();
        var msg = StageEml("118.imapfetch:2,S", eml, messageId: 118);

        var r = ext.Extract(msg, partIndex: 0);

        r.ContentType.ShouldBe("text/csv");
        r.InlineText.ShouldBeNull();
        File.Exists(r.FilePath).ShouldBeTrue();
        File.ReadAllBytes(r.FilePath).ShouldBe(invalidBytes);
    }

    [Fact]
    public void Symlink_at_target_path_is_refused()
    {
        // Stage a Maildir EML and pre-create a symlink at the target output
        // path. The extractor must refuse to overwrite (symlinks could redirect
        // the write outside the download dir).
        var ext = BuildExtractor();
        var msg = StageEml("119.imapfetch:2,S",
            BuildGenericOctetEml("safe.pdf", "JVBERi0xLjAKJSVFT0YK"),
            messageId: 119);

        // Pre-create symlink at the expected target.
        var expectedTarget = Path.Combine(_downloadDir, "119-0-safe.pdf");
        var symlinkVictim = Path.Combine(_tempRoot, "victim.txt");
        File.WriteAllText(symlinkVictim, "the attacker's target");
        File.CreateSymbolicLink(expectedTarget, symlinkVictim);

        Should.Throw<InvalidOperationException>(() => ext.Extract(msg, partIndex: 0))
            .Message.ShouldContain("symlink");

        // The symlink target wasn't overwritten.
        File.ReadAllText(symlinkVictim).ShouldBe("the attacker's target");
    }

    [Fact]
    public void DownloadDir_property_returns_expanded_path()
    {
        var ext = BuildExtractor();
        ext.DownloadDir.ShouldBe(_downloadDir);
    }
}
