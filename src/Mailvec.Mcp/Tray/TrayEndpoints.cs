using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Search;
using Mailvec.Core.Tray;
using Microsoft.Extensions.Options;
using System.Linq;

namespace Mailvec.Mcp.Tray;

/// <summary>
/// Plain REST surface that the SwiftUI tray app talks to. These endpoints are
/// deliberately NOT MCP tools — the tray isn't an LLM agent, so it shouldn't
/// pay the cost of MCP framing (session ids, SSE, JSON-RPC envelope). They
/// share the same Kestrel host as /health and the MCP endpoints, on the same
/// loopback bind address.
/// </summary>
public static class TrayEndpoints
{
    public static void MapTrayEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/tray").WithTags("tray");

        group.MapGet("/status", async (TrayStatusService svc, CancellationToken ct)
            => Results.Ok(await svc.BuildAsync(ct).ConfigureAwait(false)));

        group.MapGet("/system", async (TraySystemService svc, CancellationToken ct)
            => Results.Ok(await svc.BuildAsync(ct).ConfigureAwait(false)));

        // Lightweight folder list for the Search popover's empty state. Mirrors
        // the MCP list_folders tool's shape, minus the date-range fields that
        // the tray doesn't render.
        group.MapGet("/folders", (MessageRepository messages) =>
        {
            var stats = messages.FolderStats();
            var rows = stats.Select(s => new { folder = s.Folder, messageCount = s.MessageCount });
            return Results.Ok(new { count = stats.Count, folders = rows });
        });

        // Full message body for the inline preview in the Search popover.
        // Returns the plaintext + attachment metadata; clients open the
        // attachment bytes via /tray/attachment if the user clicks them.
        group.MapGet("/email/{id:long}", (long id, MessageRepository messages, IOptions<FastmailOptions> fastmail) =>
        {
            var msg = messages.GetById(id);
            if (msg is null) return Results.NotFound(new { error = $"message {id} not found" });
            var to = string.Join(", ", msg.ToAddresses.Select(a =>
                string.IsNullOrEmpty(a.Name) ? a.Address : $"{a.Name} <{a.Address}>"));
            var atts = msg.Attachments.Select(a => new TrayEmailAttachment(
                PartIndex: a.PartIndex,
                FileName: a.FileName,
                ContentType: a.ContentType ?? "application/octet-stream",
                Size: a.SizeBytes ?? 0)).ToList();
            return Results.Ok(new TrayEmail(
                Id: msg.Id,
                MessageId: msg.MessageId,
                Folder: msg.Folder,
                Subject: msg.Subject,
                FromAddress: msg.FromAddress,
                FromName: msg.FromName,
                To: to.Length == 0 ? null : to,
                DateSent: msg.DateSent,
                BodyText: msg.BodyText,
                HasHtml: !string.IsNullOrEmpty(msg.BodyHtml),
                Attachments: atts,
                WebmailUrl: WebmailLinkBuilder.Build(msg.MessageId, fastmail.Value)));
        });

        group.MapPost("/search", async (TraySearchRequest body, TraySearchService svc, CancellationToken ct) =>
        {
            try
            {
                var resp = await svc.SearchAsync(body, ct).ConfigureAwait(false);
                return Results.Ok(resp);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST so the action verb (kickstart / bootout) makes semantic sense.
        // Restricted to the four mailvec labels inside LaunchdInspector.
        group.MapPost("/control", async (TrayControlRequest body, LaunchdInspector inspector, CancellationToken ct) =>
        {
            var label = body.Service.StartsWith("com.mailvec.", StringComparison.Ordinal)
                ? body.Service
                : $"com.mailvec.{body.Service}";

            try
            {
                bool ok = body.Action.ToLowerInvariant() switch
                {
                    "kickstart" or "resume" => await inspector.KickstartAsync(label, ct).ConfigureAwait(false),
                    "bootout" or "pause" => await inspector.BootoutAsync(label, ct).ConfigureAwait(false),
                    _ => throw new ArgumentException($"Unknown action '{body.Action}'. Use kickstart or bootout."),
                };
                return Results.Ok(new TrayControlResponse(ok, ok ? $"{body.Action} succeeded" : $"{body.Action} failed (non-zero exit)"));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new TrayControlResponse(false, ex.Message));
            }
        });

        // Returns the file path the attachment was written to. The Swift app
        // opens it via NSWorkspace.open — we don't ship bytes over HTTP.
        // Reuses the existing AttachmentExtractor so the file lands at the
        // configured AttachmentDownloadDir (default ~/Downloads/mailvec).
        group.MapPost("/attachment", async (TrayAttachmentRequest body, MessageRepository messages, AttachmentExtractor extractor, CancellationToken ct) =>
        {
            await Task.Yield();
            var msg = messages.GetById(body.MessageId);
            if (msg is null)
            {
                return Results.NotFound(new { error = $"message {body.MessageId} not found" });
            }
            try
            {
                var result = extractor.Extract(msg, body.PartIndex);
                return Results.Ok(new TrayAttachmentResponse(
                    Path: result.FilePath,
                    Bytes: result.SizeBytes,
                    ContentType: result.ContentType,
                    WasReused: result.WasReused));
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
