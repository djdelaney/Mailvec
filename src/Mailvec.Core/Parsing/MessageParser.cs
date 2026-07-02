using Mailvec.Core.Attachments;
using Mailvec.Core.Models;
using MimeKit;

namespace Mailvec.Core.Parsing;

public sealed class MessageParser
{
    private readonly AttachmentTextExtractor? _attachmentExtractor;

    /// <summary>
    /// Parameterless ctor for tests / contexts that don't need attachment text
    /// extraction. Production code resolves the DI ctor below so attachments
    /// get content-indexed alongside the message body.
    /// </summary>
    public MessageParser() : this(null) { }

    public MessageParser(AttachmentTextExtractor? attachmentExtractor)
    {
        _attachmentExtractor = attachmentExtractor;
    }

    public ParsedMessage ParseFile(string emlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emlPath);

        using var stream = File.OpenRead(emlPath);
        var mime = MimeMessage.Load(stream);
        var size = new FileInfo(emlPath).Length;
        return Parse(mime, size);
    }

    public ParsedMessage Parse(MimeMessage mime, long sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(mime);

        var bodyHtml = mime.HtmlBody;
        // Prefer the HTML rendition when present: many senders (American
        // Airlines, marketing platforms in general) ship a text/plain
        // multipart alternative that's a broken markdown-ish dump from their
        // template engine — bare URLs in parens, leaked CSS @import lines,
        // unclosed HTML attribute fragments. V2's AngleSharp pipeline produces
        // strictly cleaner output than what those senders inline. Fall back
        // to mime.TextBody only when there's no HTML at all (genuine plain-
        // text mail). Keeps the indexer's body_text consistent with what
        // `mailvec rebuild-bodies` writes.
        var bodyText = !string.IsNullOrEmpty(bodyHtml)
            ? HtmlToText.Convert(bodyHtml)
            : mime.TextBody;

        // Strip quoted reply chains so each message contributes only its new
        // content. Without this, BM25 over-rewards replies that quote the
        // original (the matching tokens appear twice) and the same content
        // gets repeated across every thread message in the embedding space.
        if (!string.IsNullOrEmpty(bodyText))
        {
            bodyText = ReplyTrimmer.Trim(bodyText, mime.Subject);
        }

        var fromMailbox = mime.From.Mailboxes.FirstOrDefault();
        var attachments = ExtractAttachments(mime);

        var messageId = StripAngleBrackets(mime.MessageId) ?? SyntheticMessageId(mime);

        var threadId = ResolveThreadId(mime, messageId);

        return new ParsedMessage(
            MessageId: messageId,
            ThreadId: threadId,
            Subject: mime.Subject,
            FromAddress: fromMailbox?.Address,
            FromName: NormalizeName(fromMailbox?.Name),
            ToAddresses: ToAddressList(mime.To),
            CcAddresses: ToAddressList(mime.Cc),
            DateSent: mime.Date == default ? null : mime.Date,
            BodyText: bodyText,
            BodyHtml: bodyHtml,
            RawHeaders: mime.Headers.ToString() ?? string.Empty,
            SizeBytes: sizeBytes,
            ContentHash: MessageBodyHasher.Hash(mime),
            Attachments: attachments);
    }

    private IReadOnlyList<ParsedAttachment> ExtractAttachments(MimeMessage mime)
    {
        var list = new List<ParsedAttachment>();
        int index = 0;
        // MessageParts.Indexable — not mime.Attachments — so inline (cid:) images
        // get rows too. Keep this in lockstep with MaildirAttachmentReader, which
        // resolves part_index back through the same enumeration.
        foreach (var entity in MessageParts.Indexable(mime))
        {
            var fileName = entity.ContentDisposition?.FileName ?? entity.ContentType?.Name;
            var contentType = entity.ContentType?.MimeType;
            long? size = entity is MimePart part && part.Content is not null
                ? DecodedContentLength(part.Content)
                : null;

            string? extractedText = null;
            string? extractionStatus = null;
            if (_attachmentExtractor is not null)
            {
                var result = _attachmentExtractor.Extract(entity, NormalizeName(fileName), contentType, size);
                extractedText = result.Text;
                extractionStatus = result.Status;
            }

            list.Add(new ParsedAttachment(
                PartIndex: index,
                FileName: NormalizeName(fileName),
                ContentType: contentType,
                SizeBytes: size,
                ExtractedText: extractedText,
                ExtractionStatus: extractionStatus));
            index++;
        }
        return list;
    }

    /// <summary>
    /// The DECODED payload length (what <c>Open()</c> yields), not the encoded
    /// MIME stream length. size_bytes feeds the oversize cap and the image-OCR
    /// min-bytes gate, which want the real payload size — base64 inflates the
    /// encoded stream ~33%, so the encoded length over-reported and could
    /// over-skip. Streamed with a pooled buffer so a large attachment never
    /// materialises in memory, and it also fixes the old CanSeek gap (a
    /// non-seekable content stream previously stored NULL and dropped the
    /// attachment out of the image-OCR gate entirely).
    /// </summary>
    private static long DecodedContentLength(IMimeContent content)
    {
        using var stream = content.Open();
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                total += read;
            return total;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IReadOnlyList<EmailAddress> ToAddressList(InternetAddressList addresses)
    {
        if (addresses.Count == 0) return [];
        var list = new List<EmailAddress>(addresses.Count);
        foreach (var mb in addresses.Mailboxes)
        {
            list.Add(new EmailAddress(NormalizeName(mb.Name), mb.Address));
        }
        return list;
    }

    private static string? NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name;

    /// <summary>
    /// Deterministic fallback id for mail with no Message-ID header (old
    /// imports, drafts, some automated senders). Throwing here used to make
    /// these messages permanently unindexable — silently absent from all
    /// search — and, because the mtime fast-path requires a stored
    /// message_id, they were fully re-parsed (attachments re-extracted) on
    /// every periodic scan forever.
    ///
    /// Derived from stable content — originator headers plus the body-section
    /// hash — NOT the Maildir path, so an mbsync rename maps to the same id
    /// (rename detection keeps working) and every rescan of the same file
    /// upserts the same row instead of duplicating. Distinct messages that
    /// are identical in (date, from, subject, body) collapse to one row —
    /// the same semantics duplicate server-side Message-IDs already get.
    /// </summary>
    private static string SyntheticMessageId(MimeMessage mime)
    {
        var seed = string.Join('\n',
            mime.Date.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            mime.From.ToString(),
            mime.Subject ?? string.Empty,
            MessageBodyHasher.Hash(mime));
        var hex = System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)));
        return $"{hex.ToLowerInvariant()}@synthetic.mailvec.local";
    }

    private static string? StripAngleBrackets(string? messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return null;
        return messageId.Trim().Trim('<', '>');
    }

    private static string ResolveThreadId(MimeMessage mime, string ownMessageId)
    {
        // Use the root of the References chain when available; otherwise the In-Reply-To;
        // otherwise the message itself is the thread root.
        if (mime.References is { Count: > 0 })
        {
            return StripAngleBrackets(mime.References[0]) ?? ownMessageId;
        }
        if (mime.InReplyTo is { Length: > 0 })
        {
            return StripAngleBrackets(mime.InReplyTo) ?? ownMessageId;
        }
        return ownMessageId;
    }

}

public sealed record ParsedMessage(
    string MessageId,
    string ThreadId,
    string? Subject,
    string? FromAddress,
    string? FromName,
    IReadOnlyList<EmailAddress> ToAddresses,
    IReadOnlyList<EmailAddress> CcAddresses,
    DateTimeOffset? DateSent,
    string? BodyText,
    string? BodyHtml,
    string RawHeaders,
    long SizeBytes,
    string ContentHash,
    IReadOnlyList<ParsedAttachment> Attachments)
{
    public bool HasAttachments => Attachments.Count > 0;
}

public sealed record ParsedAttachment(
    int PartIndex,
    string? FileName,
    string? ContentType,
    long? SizeBytes,
    string? ExtractedText = null,
    string? ExtractionStatus = null);
