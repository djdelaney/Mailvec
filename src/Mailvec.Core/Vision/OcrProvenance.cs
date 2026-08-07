namespace Mailvec.Core.Vision;

/// <summary>
/// Sentinel values for <c>attachments.ocr_model</c> that are not engine ids.
/// </summary>
public static class OcrProvenance
{
    /// <summary>
    /// The document was retired before any vision engine saw it — an unreadable
    /// <c>.eml</c>, a PDF PDFium cannot open, bytes that do not decode as an
    /// image, or an image the dimension/aspect gate rejected as non-content.
    ///
    /// <para><b>Why not the engine id.</b> Stamping the configured engine on
    /// these would attribute a verdict to a provider that was never called, and
    /// it would poison the one query the column exists to answer: "re-OCR
    /// everything engine X produced" would sweep up corrupt <c>.eml</c> files
    /// and HEIC images, which no provider switch can fix, and it would do so
    /// forever because each retry fails at the same pre-provider step.</para>
    ///
    /// <para><b>Why not NULL either.</b> NULL means "provenance unknown"
    /// (a row that predates v10). "No engine was involved" is a different and
    /// stronger fact — it is knowledge, not absence of it — and collapsing the
    /// two would sweep these same rows into a <c>WHERE ocr_model IS NULL</c>
    /// re-OCR instead. Same reasoning as ServiceHeartbeat's "Unknown ≠ stale".</para>
    /// </summary>
    public const string PreProvider = "pipeline";
}
