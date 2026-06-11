using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Mailvec.OutlookExport;

/// <summary>
/// Attaches to the running classic (Win32) Outlook over late-bound COM and
/// exports mail folders as .eml files in a Maildir-shaped tree:
///   &lt;out&gt;/&lt;store&gt;/&lt;Folder.Sub&gt;/{tmp,new,cur}/&lt;unix&gt;.&lt;hash&gt;.outlookexport.eml
/// Late binding (dynamic over IDispatch) avoids any dependency on Office
/// interop assemblies, so the same exe works against any Outlook build.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OutlookExporter(ExportOptions options)
{
    // MAPI property tags, exposed through PropertyAccessor.GetProperty.
    private const string PrInternetMessageId = "http://schemas.microsoft.com/mapi/proptag/0x1035001F";
    private const string PrInReplyToId = "http://schemas.microsoft.com/mapi/proptag/0x1042001F";
    private const string PrInternetReferences = "http://schemas.microsoft.com/mapi/proptag/0x1039001F";
    private const string PrSenderSmtpAddress = "http://schemas.microsoft.com/mapi/proptag/0x5D01001F";
    private const string PrSmtpAddress = "http://schemas.microsoft.com/mapi/proptag/0x39FE001F";

    private const int OlMailItemClass = 43;     // OlObjectClass.olMail
    private const int OlFolderTypeMail = 0;     // OlItemType.olMailItem as DefaultItemType
    private const int OlAttachmentOle = 6;      // OlAttachmentType.olOLE

    public sealed record Totals(int Exported, int Skipped, int Failed, int AttachmentsDropped);

    private int _exported;
    private int _skipped;
    private int _failed;
    private int _attachmentsDropped;

    public Totals Run()
    {
        var progType = Type.GetTypeFromProgID("Outlook.Application")
            ?? throw new InvalidOperationException(
                "Outlook.Application ProgID not registered — is classic (Win32) Outlook installed on this machine?");

        // Outlook is single-instance: if it's already running this attaches to
        // the live, authenticated session; if not, it launches it (which may
        // show a profile prompt — start Outlook first for unattended runs).
        dynamic app = Activator.CreateInstance(progType)!;
        dynamic session = app.Session;

        if (options.AllStores)
        {
            dynamic stores = session.Stores;
            int count = stores.Count;
            for (var i = 1; i <= count; i++)
            {
                dynamic store = stores[i];
                ExportStore(store);
            }
        }
        else
        {
            ExportStore(session.DefaultStore);
        }

        return new Totals(_exported, _skipped, _failed, _attachmentsDropped);
    }

    private void ExportStore(dynamic store)
    {
        string storeName = (string?)store.DisplayName ?? "store";
        Console.WriteLine($"Store: {storeName}");
        dynamic root = store.GetRootFolder();
        // Items living directly on the store root (rare) are intentionally
        // skipped; only named folders below the root are exported.
        Recurse(root, SanitizeSegment(storeName), new List<string>());
    }

    private void Recurse(dynamic folder, string storeDir, List<string> segments)
    {
        string name = (string?)folder.Name ?? "";
        var path = segments.Count == 0 ? name : string.Join("/", segments);

        if (segments.Count > 0)
        {
            if (IsExcluded(segments)) return;
            if (IsIncluded(path) && (int)folder.DefaultItemType == OlFolderTypeMail)
            {
                ExportFolderItems(folder, storeDir, segments, path);
            }
        }

        dynamic children = folder.Folders;
        int count = children.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic child = children[i];
            string childName = (string?)child.Name ?? $"folder{i}";
            Recurse(child, storeDir, new List<string>(segments) { childName });
        }
    }

    private bool IsExcluded(List<string> segments) =>
        segments.Any(s => options.ExcludeFolders.Contains(s, StringComparer.OrdinalIgnoreCase));

    /// <summary>No --include filters means everything; otherwise the folder
    /// path must equal a filter or live underneath one ("Inbox" matches
    /// "Inbox" and "Inbox/Projects").</summary>
    private bool IsIncluded(string path)
    {
        if (options.IncludeFolders.Count == 0) return true;
        return options.IncludeFolders.Any(f =>
            path.Equals(f, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase));
    }

    private void ExportFolderItems(dynamic folder, string storeDir, List<string> segments, string path)
    {
        // mbsync-style flat dotted folder names: Inbox/Projects -> Inbox.Projects.
        // MaildirScanner walks any dir containing new/ or cur/, so the layout
        // only has to be *a* Maildir tree, not mbsync's exact one.
        var maildirName = string.Join(".", segments.Select(SanitizeSegment));
        var folderDir = Path.Combine(options.OutDir, storeDir, maildirName);
        var tmpDir = Path.Combine(folderDir, "tmp");
        var curDir = Path.Combine(folderDir, "cur");

        dynamic items = folder.Items;
        int totalCount = items.Count;

        if (options.DryRun)
        {
            Console.WriteLine($"  {path}  ({totalCount} items) -> {folderDir}");
            return;
        }

        Console.WriteLine($"  {path}  ({totalCount} items)");
        Directory.CreateDirectory(tmpDir);
        Directory.CreateDirectory(curDir);
        Directory.CreateDirectory(Path.Combine(folderDir, "new"));

        if (options.Since is { } since)
        {
            try
            {
                // Outlook's Restrict date parsing is locale-sensitive; the
                // invariant US format is the most widely accepted. A client-side
                // re-check below makes correctness independent of this filter —
                // Restrict is purely a COM-roundtrip saver on big folders.
                var filter = $"[ReceivedTime] >= '{since.ToString("MM/dd/yyyy HH:mm", CultureInfo.InvariantCulture)}'";
                items = items.Restrict(filter);
            }
            catch (COMException)
            {
                Console.WriteLine("    (Restrict failed; falling back to client-side date filtering)");
            }
        }

        dynamic? item = items.GetFirst();
        while (item is not null)
        {
            try
            {
                if ((int)item.Class == OlMailItemClass)
                {
                    ExportMessage(item, tmpDir, curDir);
                }
            }
            catch (Exception ex)
            {
                _failed++;
                string subject;
                try { subject = (string?)item.Subject ?? "(no subject)"; }
                catch { subject = "(unreadable)"; }
                Console.Error.WriteLine($"    FAILED \"{Truncate(subject, 60)}\": {ex.Message}");
            }

            dynamic? next = items.GetNext();
            Release(item);
            item = next;
        }
    }

    private void ExportMessage(dynamic mail, string tmpDir, string curDir)
    {
        var received = GetDate(mail);
        if (options.Since is { } since && received < since)
        {
            _skipped++;
            return;
        }

        var messageId = TryGetMailProp(mail, PrInternetMessageId)
            ?? $"<{Sha16((string)mail.EntryID)}@outlook-export.invalid>";

        var fileName = $"{received.ToUnixTimeSeconds()}.{Sha16(messageId)}.outlookexport.eml";
        var destPath = Path.Combine(curDir, fileName);
        if (File.Exists(destPath))
        {
            _skipped++;
            return;
        }

        var exported = Snapshot(mail, messageId, received);
        var mime = EmlBuilder.Build(exported);

        // Maildir delivery convention: write fully into tmp/, then rename
        // into cur/ so watchers and sync tools never see a partial message.
        var tmpPath = Path.Combine(tmpDir, fileName);
        using (var stream = File.Create(tmpPath))
        {
            mime.WriteTo(stream);
        }
        File.Move(tmpPath, destPath, overwrite: true);
        _exported++;
    }

    private ExportedMessage Snapshot(dynamic mail, string messageId, DateTimeOffset received)
    {
        // (object) cast keeps the call statically bound so the tuple return
        // type survives — deconstructing a dynamic call result is a CS8133.
        var (to, cc, bcc) = GetRecipients((object)mail);
        string? html = null;
        string? text;
        // OlBodyFormat: 1=plain, 2=HTML, 3=RTF. RTF keeps the plain Body
        // rendition; Mailvec's HtmlToText pipeline handles the HTML case.
        int bodyFormat;
        try { bodyFormat = (int)mail.BodyFormat; } catch { bodyFormat = 1; }
        if (bodyFormat == 2)
        {
            try { html = (string?)mail.HTMLBody; } catch { html = null; }
        }
        try { text = (string?)mail.Body; } catch { text = null; }

        return new ExportedMessage
        {
            MessageId = messageId,
            InReplyTo = TryGetMailProp(mail, PrInReplyToId),
            References = TryGetMailProp(mail, PrInternetReferences),
            FromName = TryGet(() => (string?)mail.SenderName),
            FromAddress = GetSenderSmtp(mail),
            To = to,
            Cc = cc,
            Bcc = bcc,
            Subject = TryGet(() => (string?)mail.Subject) ?? "",
            Date = GetSentDate(mail, received),
            TextBody = text,
            HtmlBody = html,
            Attachments = GetAttachments(mail, tmpRoot: Path.GetTempPath()),
        };
    }

    private string? GetSenderSmtp(dynamic mail)
    {
        var smtp = TryGetMailProp(mail, PrSenderSmtpAddress);
        if (!string.IsNullOrWhiteSpace(smtp)) return smtp;

        var type = TryGet(() => (string?)mail.SenderEmailType);
        if (string.Equals(type, "EX", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                dynamic sender = mail.Sender;
                dynamic? exUser = sender.GetExchangeUser();
                if (exUser is not null)
                {
                    var addr = (string?)exUser.PrimarySmtpAddress;
                    if (!string.IsNullOrWhiteSpace(addr)) return addr;
                }
            }
            catch
            {
                // fall through to the raw (possibly X.500) address below
            }
        }
        return TryGet(() => (string?)mail.SenderEmailAddress);
    }

    private (List<ExportedAddress> To, List<ExportedAddress> Cc, List<ExportedAddress> Bcc) GetRecipients(dynamic mail)
    {
        var to = new List<ExportedAddress>();
        var cc = new List<ExportedAddress>();
        var bcc = new List<ExportedAddress>();
        try
        {
            dynamic recipients = mail.Recipients;
            int count = recipients.Count;
            for (var i = 1; i <= count; i++)
            {
                dynamic r = recipients[i];
                string? name = TryGet(() => (string?)r.Name);
                string? addr = null;
                try
                {
                    dynamic pa = r.PropertyAccessor;
                    addr = (string?)pa.GetProperty(PrSmtpAddress);
                }
                catch
                {
                    // not all recipients resolve to SMTP (e.g. external one-off entries)
                }
                addr = string.IsNullOrWhiteSpace(addr) ? TryGet(() => (string?)r.Address) : addr;
                var entry = new ExportedAddress(name, addr ?? "unknown@unresolved.invalid");
                // OlMailRecipientType: 1=To, 2=CC, 3=BCC
                int rtype = TryGet(() => (int?)r.Type) ?? 1;
                (rtype switch { 2 => cc, 3 => bcc, _ => to }).Add(entry);
            }
        }
        catch
        {
            // recipient table unreadable — export with empty To rather than dropping the message
        }
        return (to, cc, bcc);
    }

    private List<ExportedAttachment> GetAttachments(dynamic mail, string tmpRoot)
    {
        var result = new List<ExportedAttachment>();
        try
        {
            dynamic attachments = mail.Attachments;
            int count = attachments.Count;
            for (var i = 1; i <= count; i++)
            {
                dynamic att = attachments[i];
                try
                {
                    if ((int)att.Type == OlAttachmentOle)
                    {
                        _attachmentsDropped++;
                        continue;
                    }
                    long size = (int)att.Size;
                    if (size > options.MaxAttachmentBytes)
                    {
                        _attachmentsDropped++;
                        continue;
                    }
                    string attName = TryGet(() => (string?)att.FileName) ?? $"attachment-{i}";
                    var tmpFile = Path.Combine(tmpRoot, $"mailvec-att-{Guid.NewGuid():N}");
                    try
                    {
                        att.SaveAsFile(tmpFile);
                        result.Add(new ExportedAttachment(attName, File.ReadAllBytes(tmpFile)));
                    }
                    finally
                    {
                        try { File.Delete(tmpFile); } catch { /* temp cleanup best-effort */ }
                    }
                }
                catch
                {
                    _attachmentsDropped++;
                }
            }
        }
        catch
        {
            // attachment table unreadable — body still exports
        }
        return result;
    }

    private static DateTimeOffset GetDate(dynamic mail)
    {
        var dt = TryGet(() => (DateTime?)mail.ReceivedTime)
            ?? TryGet(() => (DateTime?)mail.SentOn)
            ?? DateTime.Now;
        return ToLocalOffset(dt);
    }

    private static DateTimeOffset GetSentDate(dynamic mail, DateTimeOffset fallback)
    {
        var dt = TryGet(() => (DateTime?)mail.SentOn);
        return dt is null ? fallback : ToLocalOffset(dt.Value);
    }

    /// <summary>COM hands back Kind=Unspecified DateTimes that are in fact
    /// local wall-clock time; stamp them as such.</summary>
    private static DateTimeOffset ToLocalOffset(DateTime dt) =>
        new(DateTime.SpecifyKind(dt, DateTimeKind.Local));

    private static string? TryGetMailProp(dynamic mail, string proptag)
    {
        try
        {
            dynamic pa = mail.PropertyAccessor;
            var value = (string?)pa.GetProperty(proptag);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static T? TryGet<T>(Func<T?> getter)
    {
        try { return getter(); }
        catch { return default; }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.ReleaseComObject(com);
        }
    }

    private static string Sha16(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>Folder/store names become directory names; strip the
    /// characters Windows forbids plus '.' (our folder-nesting separator).</summary>
    internal static string SanitizeSegment(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or '.' || char.IsControl(ch)
                ? '_'
                : ch);
        }
        var cleaned = sb.ToString().Trim(' ', '_');
        return cleaned.Length == 0 ? "folder" : cleaned;
    }
}
