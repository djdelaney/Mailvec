using System.CommandLine;
using Mailvec.Core.Data;
using Mailvec.Core.Models;
using Mailvec.Core.Options;
using Mailvec.Core.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Look up a single message by either its internal SQLite id (numeric) or its
/// RFC 5322 Message-ID (anything else). Mirrors the MCP `get_email` tool.
/// </summary>
internal static class GetCommand
{
    public static Command Build()
    {
        var idArg = new Argument<string>("id") { Description = "Internal SQLite id (numeric) or RFC 5322 Message-ID (e.g. '<abc@example.com>')." };
        var bodyOpt = new Option<bool>("--body", "-b") { Description = "Print the full plain-text body instead of a 1KB excerpt." };

        var cmd = new Command("get", "Fetch a single message by internal id or RFC Message-ID.")
        {
            idArg,
            bodyOpt,
        };

        cmd.SetAction(parseResult => Run(
            parseResult.GetValue(idArg)!,
            parseResult.GetValue(bodyOpt)));
        return cmd;
    }

    private static int Run(string id, bool fullBody)
    {
        using var sp = CliServices.Build();
        return Execute(sp, id, fullBody, Console.Out, Console.Error);
    }

    /// <summary>Test seam — see <see cref="PurgeDeletedCommand"/> for the pattern.</summary>
    internal static int Execute(IServiceProvider sp, string id, bool fullBody, TextWriter @out, TextWriter err)
    {
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        var repo = sp.GetRequiredService<MessageRepository>();
        var fastmail = sp.GetRequiredService<IOptions<FastmailOptions>>().Value;

        var message = long.TryParse(id, out var numeric)
            ? repo.GetById(numeric)
            // Strip angle brackets so a header-style "<id@host>" paste works.
            // Stored message_id (and eval-add input) is the bare form.
            : repo.GetByMessageId(id.Trim().TrimStart('<').TrimEnd('>'));

        if (message is null)
        {
            err.WriteLine($"No message found for id '{id}'.");
            return 1;
        }

        Print(@out, message, fullBody, fastmail);
        return 0;
    }

    private static void Print(TextWriter @out, Message m, bool fullBody, FastmailOptions fastmail)
    {
        var date = m.DateSent?.ToString("u") ?? "(no date)";
        var from = m.FromName is null ? m.FromAddress : $"{m.FromName} <{m.FromAddress}>";

        @out.WriteLine($"id:        {m.Id}");
        @out.WriteLine($"messageId: {m.MessageId}");
        if (m.ThreadId is not null) @out.WriteLine($"threadId:  {m.ThreadId}");
        @out.WriteLine($"folder:    {m.Folder}");
        @out.WriteLine($"date:      {date}");
        @out.WriteLine($"from:      {from ?? "(unknown)"}");
        if (m.ToAddresses.Count > 0) @out.WriteLine($"to:        {FormatAddresses(m.ToAddresses)}");
        if (m.CcAddresses.Count > 0) @out.WriteLine($"cc:        {FormatAddresses(m.CcAddresses)}");
        @out.WriteLine($"subject:   {m.Subject ?? "(no subject)"}");

        var url = WebmailLinkBuilder.Build(m.MessageId, fastmail);
        if (url is not null) @out.WriteLine($"webmail:   {url}");

        if (m.Attachments.Count > 0)
        {
            @out.WriteLine();
            @out.WriteLine($"attachments ({m.Attachments.Count}):");
            foreach (var a in m.Attachments)
            {
                var size = a.SizeBytes is { } b ? $"{b:N0} B" : "?";
                var status = a.ExtractionStatus is null ? "" : $"  [{a.ExtractionStatus}]";
                @out.WriteLine($"  partIndex={a.PartIndex}  {a.FileName ?? "(unnamed)"}  {a.ContentType ?? "?"}  {size}{status}");
            }
        }

        if (!string.IsNullOrEmpty(m.BodyText))
        {
            @out.WriteLine();
            @out.WriteLine("body:");
            @out.WriteLine(fullBody || m.BodyText.Length <= 1024 ? m.BodyText : m.BodyText[..1024] + "\n…(truncated; pass --body for full text)");
        }
    }

    private static string FormatAddresses(IReadOnlyList<EmailAddress> addrs) =>
        string.Join(", ", addrs.Select(a => a.Name is null ? a.Address : $"{a.Name} <{a.Address}>"));
}
