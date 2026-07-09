using MimeKit;

namespace Mailvec.Core.Attachments;

/// <summary>
/// The single source of truth for which MIME parts of a message get an
/// <c>attachments</c> row and how <c>part_index</c> maps to a part. Both the
/// write side (<see cref="Parsing.MessageParser"/>, which assigns part_index at
/// index time) and the read side (<see cref="MaildirAttachmentReader"/>, which
/// resolves part_index back to bytes) MUST enumerate through here, or the two
/// drift and view_attachment / OCR read the wrong bytes.
///
/// The set is: every <c>Content-Disposition: attachment</c> part (MimeKit's
/// <see cref="MimeMessage.Attachments"/>, in document order) FIRST, then every
/// inline image part not already covered, in document order. MimeKit's
/// <c>Attachments</c> excludes inline (<c>cid:</c>) images by design, so a
/// photographed document embedded inline — extremely common from Apple Mail and
/// forwarded posts — otherwise never gets a row and is invisible to search/OCR.
///
/// **Ordering is deliberate**: inline images are *appended after* the
/// attachments rather than interleaved into true document order. That keeps the
/// part_index of every existing attachment row unchanged, so adding inline
/// capture is a schema-additive backfill (insert the new higher indices) rather
/// than a full reindex — an interleave would renumber existing rows and silently
/// point them at the wrong bytes.
/// </summary>
public static class MessageParts
{
    /// <summary>
    /// Ordered parts that map to <c>part_index</c> 0..N-1. Deterministic across
    /// repeated parses of the same bytes (both source enumerations are
    /// document-order tree walks), which is what makes the writer/reader
    /// agreement hold.
    /// </summary>
    public static IReadOnlyList<MimeEntity> Indexable(MimeMessage mime)
    {
        ArgumentNullException.ThrowIfNull(mime);

        var attachments = mime.Attachments.ToList();
        // Reference identity: Attachments and BodyParts return the same entity
        // instances from the one parsed tree, so a HashSet with the default
        // (reference) comparer correctly excludes parts already counted.
        var seen = new HashSet<MimeEntity>(attachments);

        List<MimeEntity>? inlineImages = null;
        foreach (var part in mime.BodyParts.OfType<MimePart>())
        {
            if (seen.Contains(part)) continue;
            if (!IsImage(part)) continue;
            (inlineImages ??= []).Add(part);
        }

        if (inlineImages is null) return attachments;

        var combined = new List<MimeEntity>(attachments.Count + inlineImages.Count);
        combined.AddRange(attachments);
        combined.AddRange(inlineImages);
        return combined;
    }

    private static bool IsImage(MimePart part) =>
        string.Equals(part.ContentType.MediaType, "image", StringComparison.OrdinalIgnoreCase);
}
