using MimeKit;
using MimeKit.Utils;

namespace Mailvec.OutlookExport;

/// <summary>
/// Builds an RFC822 MimeMessage from an <see cref="ExportedMessage"/> snapshot.
/// Pure — no COM, no filesystem — so the address/header edge cases can be
/// unit-tested without Outlook.
/// </summary>
public static class EmlBuilder
{
    public static MimeMessage Build(ExportedMessage m)
    {
        var msg = new MimeMessage();

        msg.From.Add(ToMailbox(m.FromName, m.FromAddress));
        foreach (var a in m.To) msg.To.Add(ToMailbox(a.Name, a.Address));
        foreach (var a in m.Cc) msg.Cc.Add(ToMailbox(a.Name, a.Address));
        foreach (var a in m.Bcc) msg.Bcc.Add(ToMailbox(a.Name, a.Address));

        msg.Subject = m.Subject;
        msg.Date = m.Date;

        // EnumerateReferences tolerates surrounding angle brackets, comments,
        // and junk; MimeMessage.MessageId wants the bare addr-spec.
        var mid = MimeUtils.EnumerateReferences(m.MessageId).FirstOrDefault();
        if (mid is not null) msg.MessageId = mid;

        if (m.InReplyTo is not null)
        {
            var irt = MimeUtils.EnumerateReferences(m.InReplyTo).FirstOrDefault();
            if (irt is not null) msg.InReplyTo = irt;
        }

        if (m.References is not null)
        {
            foreach (var r in MimeUtils.EnumerateReferences(m.References))
            {
                msg.References.Add(r);
            }
        }

        var builder = new BodyBuilder();
        if (!string.IsNullOrEmpty(m.HtmlBody)) builder.HtmlBody = m.HtmlBody;
        if (!string.IsNullOrEmpty(m.TextBody)) builder.TextBody = m.TextBody;

        foreach (var att in m.Attachments)
        {
            builder.Attachments.Add(att.FileName, att.Data);
        }

        msg.Body = builder.ToMessageBody();
        return msg;
    }

    /// <summary>
    /// Exchange-internal senders sometimes come through as X.500 DNs
    /// (/O=ORG/OU=...) when SMTP resolution fails. MailboxAddress accepts
    /// most strings verbatim, but parse failures fall back to a sentinel
    /// rather than dropping the message.
    /// </summary>
    private static MailboxAddress ToMailbox(string? name, string? address)
    {
        var addr = string.IsNullOrWhiteSpace(address) ? "unknown@unresolved.invalid" : address.Trim();
        try
        {
            return new MailboxAddress(name ?? "", addr);
        }
        catch (ParseException)
        {
            return new MailboxAddress(name ?? addr, "unknown@unresolved.invalid");
        }
    }
}
