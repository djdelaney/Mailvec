using System.Text;
using Mailvec.Core.Models;
using MimeKit;
using MimeKit.Text;

namespace Mailvec.Core.Parsing;

public sealed class MessageParser
{
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

        var bodyText = mime.TextBody;
        var bodyHtml = mime.HtmlBody;

        // If only HTML is present, derive plain text from it so FTS5 has something to chew on.
        if (string.IsNullOrEmpty(bodyText) && !string.IsNullOrEmpty(bodyHtml))
        {
            bodyText = ConvertHtmlToText(bodyHtml);
        }

        var fromMailbox = mime.From.Mailboxes.FirstOrDefault();
        var attachments = ExtractAttachments(mime);

        var messageId = StripAngleBrackets(mime.MessageId)
            ?? throw new InvalidOperationException("Message has no Message-ID header.");

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
            Attachments: attachments);
    }

    private static IReadOnlyList<ParsedAttachment> ExtractAttachments(MimeMessage mime)
    {
        var list = new List<ParsedAttachment>();
        int index = 0;
        foreach (var entity in mime.Attachments)
        {
            var fileName = entity.ContentDisposition?.FileName ?? entity.ContentType?.Name;
            var contentType = entity.ContentType?.MimeType;
            long? size = entity is MimePart part && part.Content is { Stream: { } s } && s.CanSeek
                ? s.Length
                : null;

            list.Add(new ParsedAttachment(
                PartIndex: index,
                FileName: NormalizeName(fileName),
                ContentType: contentType,
                SizeBytes: size));
            index++;
        }
        return list;
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

    private static string ConvertHtmlToText(string html)
    {
        // MimeKit ships HtmlTokenizer but not a HtmlToText converter, so we
        // walk the token stream and emit text content. Quality is "good
        // enough for FTS/embeddings"; revisit if marketing email noise hurts.
        var tokenizer = new HtmlTokenizer(new StringReader(html));
        var sb = new StringBuilder(html.Length);
        bool insideScript = false;
        bool insideStyle = false;

        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token.Kind)
            {
                case HtmlTokenKind.Tag:
                    var tag = (HtmlTagToken)token;
                    var name = tag.Name?.ToLowerInvariant();
                    if (name == "script") insideScript = !tag.IsEndTag && !tag.IsEmptyElement;
                    else if (name == "style") insideStyle = !tag.IsEndTag && !tag.IsEmptyElement;
                    else if (name is "p" or "br" or "div" or "li" or "tr" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6") sb.Append('\n');
                    break;
                case HtmlTokenKind.Data when !insideScript && !insideStyle:
                    sb.Append(((HtmlDataToken)token).Data);
                    break;
            }
        }

        return CollapseWhitespace(sb.ToString());
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool prevWasSpace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (ch == '\n') { sb.Append('\n'); prevWasSpace = false; }
                else if (!prevWasSpace) { sb.Append(' '); prevWasSpace = true; }
            }
            else
            {
                sb.Append(ch);
                prevWasSpace = false;
            }
        }
        return sb.ToString().Trim();
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
    IReadOnlyList<ParsedAttachment> Attachments)
{
    public bool HasAttachments => Attachments.Count > 0;
}

public sealed record ParsedAttachment(
    int PartIndex,
    string? FileName,
    string? ContentType,
    long? SizeBytes);
