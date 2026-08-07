namespace Mailvec.Core.Data;

/// <summary>
/// SQL fragments shared by every query that has to decide what an OCR verdict
/// is. One string, because two copies drifted once already: <c>reocr</c> counted
/// an image at <c>no_text</c> as a completed verdict while
/// <c>backfill-ocr-model</c> refused to stamp one, so <c>--engine unknown</c>
/// selected 1,379 rows the backfill had left NULL.
/// </summary>
public static class AttachmentOcrSql
{
    /// <summary>
    /// Attachments the image-OCR pass owns. Aliased <c>a</c>, matching every
    /// caller's FROM clause.
    ///
    /// <para>content_type <c>image/*</c> minus GIF (animated/banner strips, low
    /// text yield). Senders also ship real photos as
    /// <c>application/octet-stream</c> or with no Content-Type at all, so a
    /// decodable image extension on a generic type qualifies too;
    /// <c>ImageRenderer.TryNormalize</c> is the backstop that marks any
    /// non-image binary 'failed'.</para>
    /// </summary>
    public const string ImageMatch = """
        (
          (lower(a.content_type) LIKE 'image/%' AND lower(a.content_type) <> 'image/gif')
          OR (
            (a.content_type IS NULL OR lower(a.content_type) IN ('application/octet-stream', ''))
            AND (
              lower(a.filename) LIKE '%.png' OR lower(a.filename) LIKE '%.jpg'
              OR lower(a.filename) LIKE '%.jpeg' OR lower(a.filename) LIKE '%.webp'
              OR lower(a.filename) LIKE '%.bmp' OR lower(a.filename) LIKE '%.tif'
              OR lower(a.filename) LIKE '%.tiff'
            )
          )
        )
        """;

    /// <summary>
    /// Statuses that unambiguously record a vision engine's verdict, so a
    /// provenance backfill may stamp them.
    ///
    /// <para><c>'ocr'</c> always qualifies. An image at <c>'no_text'</c> also
    /// does, and this is the part that is easy to get wrong: the INDEXER can
    /// never put an image there — <c>ResolveFormat</c> classifies images as
    /// Unsupported, and <c>BuildResult</c>'s <c>no_text</c> only fires for
    /// document formats it actually parsed — so the only writer is
    /// <c>MarkAttachmentImageNoText</c>. A PDF at <c>no_text</c> is the opposite:
    /// that IS indexer output (a scanned PDF it could not read natively) and must
    /// never be stamped.</para>
    /// </summary>
    public const string BackfillableVerdict = $"""
        (
          a.extraction_status = 'ocr'
          OR ({ImageMatch} AND a.extraction_status = 'no_text')
        )
        """;
}
