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
    /// The queue directory shared with the mbsync sidecar
    /// (<c>/mark-read</c> in <c>compose.mark-read.yml</c>). mcp reads the
    /// sidecar's marker from it and writes requests into its <c>inbox/</c>
    /// subdirectory only. Empty — the
    /// default — leaves <c>mark_read</c> unregistered: absent from tools/list,
    /// not present-and-refusing, and the server instructions keep saying the
    /// surface is read-only.
    /// </summary>
    public string QueueDirectory { get; set; } = "";

    /// <summary>
    /// Refuse to queue anything while this process can write to
    /// <see cref="QueueDirectory"/> itself (as opposed to its <c>inbox/</c>).
    /// The sidecar writes its marker and records there, both containers run
    /// as one uid, and the read-only mount is the only thing that stops a
    /// compromised mcp planting a link for the sidecar to write through. A
    /// deployment that took new images but kept an old overlay file would
    /// otherwise work exactly as before with that protection silently
    /// absent. Turn it off only where no sidecar shares the directory (a test).
    /// </summary>
    public bool RequireReadOnlyQueue { get; set; } = true;

    /// <summary>Most messages one <c>mark_read</c> call may name.</summary>
    public int MaxMessagesPerCall { get; set; } = 100;

    public bool Enabled => !string.IsNullOrWhiteSpace(QueueDirectory);
}
