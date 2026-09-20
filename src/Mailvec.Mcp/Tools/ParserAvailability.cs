using Mailvec.Parsing.Contracts;

namespace Mailvec.Mcp.Tools;

/// <summary>
/// The one parser failure the viewer tools answer differently: the parse
/// service being down (<see cref="ParseFailureKind.Unavailable"/>). Every
/// other parser failure keeps the tools' existing per-document answer, and
/// none of this changes a tool name, parameter or response field — it is
/// message text only, so the wire contract (McpSurfaceTests) is untouched.
/// </summary>
internal static class ParserAvailability
{
    /// <summary>
    /// Stable text, like the "Could not render" message: it names the
    /// remedy and nothing about the transport. The reason (a connection
    /// error, a timeout) is deployment state the remote caller cannot act
    /// on; it goes to the log.
    /// </summary>
    public const string Message =
        "Attachment parsing is temporarily unavailable; retry in a moment, " +
        "or call get_attachment_text for the document's extracted text.";

    public static bool IsOutage(Exception ex) => ex is ParseException { Kind: ParseFailureKind.Unavailable };
}
