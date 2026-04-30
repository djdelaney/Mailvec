using System.ComponentModel;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mailvec.Mcp.Tools;

/// <summary>
/// Extracts an attachment to ~/Downloads/mailvec/ (configurable) and returns
/// the absolute path. We do NOT try to ship binary bytes back through MCP —
/// Claude.ai's bridge maps EmbeddedResourceBlock to a Claude API image
/// content block regardless of MIME, then rejects non-image MIMEs as
/// "unsupported image format". Putting the file on disk delegates the
/// "interpret bytes by file type" job to whichever tool is best at it:
///   - Claude Code's built-in Read can open the file directly (PDFs, text,
///     images, etc.).
///   - On Claude.ai / Claude Desktop, a filesystem MCP server pointed at the
///     download dir picks up the read.
/// For images and small text-ish files we ALSO inline the content as native
/// MCP blocks (ImageContentBlock / TextContentBlock) so simple "what's in
/// this CSV" / "describe this photo" cases work without a second tool.
/// </summary>
[McpServerToolType]
public sealed class GetAttachmentTool(
    MessageRepository messages,
    AttachmentExtractor extractor,
    ToolCallLogger callLog)
{
    private const string ToolName = "get_attachment";

    [McpServerTool(Name = "get_attachment")]
    [Description(
        "Extract a single attachment from an email and save it to a user-visible directory (default ~/Downloads/mailvec/). " +
        "Identify the email with either `id` (the internal SQLite id) OR `messageId` (the RFC Message-ID). " +
        "Identify the attachment with `partIndex` from the get_email response (0-based, in MIME order). " +
        "The response always includes the absolute path to the saved file. To read the contents: " +
        "in Claude Code, use the Read tool on that path; on Claude.ai / Claude Desktop, point a filesystem " +
        "MCP server (e.g. @modelcontextprotocol/server-filesystem) at the download directory and call its read tool. " +
        "For convenience, image attachments are also returned inline as an MCP ImageContentBlock " +
        "(visible to Claude vision), and small text-ish files (text/*, application/json, etc., under ~256 KB) " +
        "have their decoded UTF-8 text included as a separate text block.")]
    public CallToolResult GetAttachment(
        [Description("0-based index from the Attachments list returned by get_email.")]
        int partIndex,
        [Description("Internal SQLite id of the email, as returned in search_emails / get_email results. Mutually exclusive with messageId.")]
        long? id = null,
        [Description("RFC Message-ID header (without angle brackets). Mutually exclusive with id.")]
        string? messageId = null)
    {
        var startTs = callLog.LogCall(ToolName, new { id, messageId, partIndex });

        if (id is null && string.IsNullOrWhiteSpace(messageId))
            throw new McpException("Provide either id or messageId.");
        if (id is not null && !string.IsNullOrWhiteSpace(messageId))
            throw new McpException("Pass id OR messageId, not both.");

        var msg = id is not null ? messages.GetById(id.Value) : messages.GetByMessageId(messageId!);
        if (msg is null)
            throw new McpException(id is not null
                ? $"No message with id {id}."
                : $"No message with Message-ID '{messageId}'.");

        if (msg.DeletedAt is not null)
            throw new McpException($"Message {msg.Id} is soft-deleted (gone from disk).");

        if (!msg.HasAttachments)
            throw new McpException($"Message {msg.Id} has no attachments.");

        ExtractResult result;
        try
        {
            result = extractor.Extract(msg, partIndex);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }

        var content = new List<ContentBlock>
        {
            new TextContentBlock { Text = BuildSummary(result) },
        };

        // Inline the decoded text for small text-ish files. Lets Claude
        // read CSV / JSON / logs in one round trip without invoking a
        // filesystem MCP.
        if (result.InlineText is not null)
        {
            content.Add(new TextContentBlock { Text = result.InlineText });
        }

        // Inline images as ImageContentBlock so Claude vision works
        // immediately. This is the only binary path that's reliable across
        // all current Claude clients — non-image binary goes through a
        // broken bridge that rejects everything as an unsupported image.
        if (IsImageContentType(result.ContentType))
        {
            try
            {
                var bytes = File.ReadAllBytes(result.FilePath);
                // Blob/Data setters take ReadOnlyMemory<byte> of the base64
                // string's UTF-8 encoding (per the SDK doc — counterintuitive).
                var base64Utf8 = System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(bytes));
                content.Add(new ImageContentBlock
                {
                    Data = base64Utf8,
                    MimeType = result.ContentType,
                });
            }
            catch (IOException)
            {
                // We just wrote the file; if reading it back fails, swallow —
                // the saved-to-disk path is the primary signal anyway.
            }
        }

        callLog.LogResult(ToolName, new
        {
            fileName = result.FileName,
            contentType = result.ContentType,
            sizeBytes = result.SizeBytes,
            filePath = result.FilePath,
            wasReused = result.WasReused,
            inlineChars = result.InlineText?.Length,
        }, startTs);

        return new CallToolResult { Content = content };
    }

    private static string BuildSummary(ExtractResult result)
    {
        var sizeStr = FormatSize(result.SizeBytes);
        var verb = result.WasReused ? "Already saved" : "Saved";
        return
            $"{verb} '{result.FileName}' ({result.ContentType}, {sizeStr}) to:\n" +
            $"  {result.FilePath}\n\n" +
            $"To read its contents:\n" +
            $"- In Claude Code, call the Read tool on the path above.\n" +
            $"- On Claude.ai / Claude Desktop, use a filesystem MCP server " +
            $"(e.g. @modelcontextprotocol/server-filesystem) pointed at the " +
            $"directory containing this file, then call its read tool.";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:N1} KB";
        return $"{kb / 1024:N1} MB";
    }

    private static bool IsImageContentType(string contentType) =>
        contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
