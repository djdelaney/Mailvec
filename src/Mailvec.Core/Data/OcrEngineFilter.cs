namespace Mailvec.Core.Data;

/// <summary>
/// Sentinel for selecting OCR rows by provenance in <c>reocr</c>.
/// </summary>
public static class OcrEngineFilter
{
    /// <summary>
    /// Selects rows whose <c>ocr_model</c> is NULL — output that predates v10,
    /// where no engine was recorded. Spelled as a word rather than requiring the
    /// operator to express SQL NULL on a command line.
    /// </summary>
    public const string Unknown = "unknown";

    public static bool IsUnknown(string? value) =>
        string.Equals(value, Unknown, StringComparison.OrdinalIgnoreCase);
}
