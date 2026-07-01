using System.Text;
using Mailvec.Core.Attachments;
using MimeKit;

namespace Mailvec.Core.Tests.Attachments;

/// <summary>
/// Locks the invariant that <see cref="MessageParts.Indexable"/> — the shared
/// writer/reader enumeration — captures inline (cid:) images, and *appends* them
/// after the real attachments so existing attachment part_index values never
/// shift. If this ordering changes, every attachment row written before the
/// change silently resolves to the wrong bytes.
/// </summary>
public class MessagePartsTests
{
    // multipart/mixed [ multipart/related [ text/html(cid ref), inline image/png ],
    //                   text/plain attachment ].
    // MimeKit's mime.Attachments = [attach.txt] only (inline excluded). The inline
    // PNG (base64 "IMGDATA") must appear via Indexable, at the index AFTER the
    // attachment.
    private const string Eml = """
        Message-ID: <inline@example.com>
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
        Content-Type: text/plain; name="attach.txt"
        Content-Disposition: attachment; filename="attach.txt"

        ATTACH-BYTES
        --outer--
        """;

    private static MimeMessage Load(string eml)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(eml));
        return MimeMessage.Load(ms);
    }

    [Fact]
    public void Indexable_appends_inline_image_after_the_attachment()
    {
        var parts = MessageParts.Indexable(Load(Eml));

        parts.Count.ShouldBe(2);
        // Index 0 stays the real attachment (mime.Attachments ordering preserved).
        ((MimePart)parts[0]).FileName.ShouldBe("attach.txt");
        // Inline image appended at the next index.
        var inline = (MimePart)parts[1];
        inline.FileName.ShouldBe("inline.png");
        inline.ContentType.MediaType.ShouldBe("image");
    }

    [Fact]
    public void Indexable_ignores_non_image_body_parts()
    {
        // text/html and text/plain body parts must NOT become indexable parts —
        // only the attachment + the inline image.
        var parts = MessageParts.Indexable(Load(Eml));
        foreach (var part in parts.Cast<MimePart>())
        {
            var isAttachment = part.FileName == "attach.txt";
            var isImage = part.ContentType.MediaType == "image";
            (isAttachment || isImage).ShouldBeTrue($"unexpected indexable part {part.ContentType.MimeType}");
        }
    }

    [Fact]
    public void Indexable_returns_only_attachments_when_there_are_no_inline_images()
    {
        const string plain = """
            Message-ID: <p@example.com>
            From: a@example.com
            To: b@example.com
            Subject: s
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/plain

            body
            --b
            Content-Type: text/plain; name="a.txt"
            Content-Disposition: attachment; filename="a.txt"

            BYTES
            --b--
            """;

        var parts = MessageParts.Indexable(Load(plain));
        parts.Count.ShouldBe(1);
        ((MimePart)parts[0]).FileName.ShouldBe("a.txt");
    }
}
