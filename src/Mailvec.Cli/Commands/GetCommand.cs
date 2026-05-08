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

        cmd.SetAction(parseResult =>
        {
            var id = parseResult.GetValue(idArg)!;
            var body = parseResult.GetValue(bodyOpt);
            return Run(id, body);
        });
        return cmd;
    }

    private static int Run(string id, bool fullBody)
    {
        using var sp = CliServices.Build();
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
            Console.Error.WriteLine($"No message found for id '{id}'.");
            return 1;
        }

        Print(message, fullBody, fastmail);
        return 0;
    }

    private static void Print(Message m, bool fullBody, FastmailOptions fastmail)
    {
        var date = m.DateSent?.ToString("u") ?? "(no date)";
        var from = m.FromName is null ? m.FromAddress : $"{m.FromName} <{m.FromAddress}>";

        Console.WriteLine($"id:        {m.Id}");
        Console.WriteLine($"messageId: {m.MessageId}");
        if (m.ThreadId is not null) Console.WriteLine($"threadId:  {m.ThreadId}");
        Console.WriteLine($"folder:    {m.Folder}");
        Console.WriteLine($"date:      {date}");
        Console.WriteLine($"from:      {from ?? "(unknown)"}");
        if (m.ToAddresses.Count > 0) Console.WriteLine($"to:        {FormatAddresses(m.ToAddresses)}");
        if (m.CcAddresses.Count > 0) Console.WriteLine($"cc:        {FormatAddresses(m.CcAddresses)}");
        Console.WriteLine($"subject:   {m.Subject ?? "(no subject)"}");

        var url = WebmailLinkBuilder.Build(m.MessageId, fastmail);
        if (url is not null) Console.WriteLine($"webmail:   {url}");

        if (m.Attachments.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"attachments ({m.Attachments.Count}):");
            foreach (var a in m.Attachments)
            {
                var size = a.SizeBytes is { } b ? $"{b:N0} B" : "?";
                var status = a.ExtractionStatus is null ? "" : $"  [{a.ExtractionStatus}]";
                Console.WriteLine($"  partIndex={a.PartIndex}  {a.FileName ?? "(unnamed)"}  {a.ContentType ?? "?"}  {size}{status}");
            }
        }

        if (!string.IsNullOrEmpty(m.BodyText))
        {
            Console.WriteLine();
            Console.WriteLine("body:");
            Console.WriteLine(fullBody || m.BodyText.Length <= 1024 ? m.BodyText : m.BodyText[..1024] + "\n…(truncated; pass --body for full text)");
        }
    }

    private static string FormatAddresses(IReadOnlyList<EmailAddress> addrs) =>
        string.Join(", ", addrs.Select(a => a.Name is null ? a.Address : $"{a.Name} <{a.Address}>"));
}
