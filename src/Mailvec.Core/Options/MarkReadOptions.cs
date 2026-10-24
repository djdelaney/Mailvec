namespace Mailvec.Core.Options;

/// <summary>
/// <c>Mcp:MarkRead</c> — the optional component that lets <c>mark_read</c>
/// change a message's seen flag. See docs/proposals/read-state.md.
///
/// <para>mcp never writes the Maildir and holds no mail credential. It writes
/// <b>requests</b> into <see cref="QueueDirectory"/>; the mbsync sidecar (the
/// one Maildir writer and the one credential holder) re-validates and applies
/// them between syncs, and mbsync's <c>PushFlags</c> carries them upstream.
/// The folder allowlist is configured once, on the sidecar
/// (<c>MBSYNC_MARK_READ_FOLDERS</c>), and published to mcp in the queue's
/// applier marker — there is deliberately no mcp-side copy to drift.</para>
/// </summary>
public sealed class MarkReadOptions
{
    /// <summary>
    /// The request queue shared with the mbsync sidecar
    /// (<c>/mark-read</c> in <c>compose.mark-read.yml</c>). Empty — the
    /// default — leaves <c>mark_read</c> unregistered: absent from tools/list,
    /// not present-and-refusing, and the server instructions keep saying the
    /// surface is read-only.
    /// </summary>
    public string QueueDirectory { get; set; } = "";

    /// <summary>Most messages one <c>mark_read</c> call may name.</summary>
    public int MaxMessagesPerCall { get; set; } = 100;

    public bool Enabled => !string.IsNullOrWhiteSpace(QueueDirectory);
}
