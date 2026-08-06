using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mailvec.OcrBench;

/// <summary>
/// Turns two strings into comparable numbers.
///
/// The normalisation is the load-bearing part. mistral-ocr emits markdown
/// (headings, pipe tables, bold) and PdfPig emits raw text; qwen emits
/// something in between. Scored raw, the winner would be whichever engine
/// happened to punctuate like the reference — measuring formatting, not
/// transcription. So both sides are flattened to lowercase alphanumerics and
/// single spaces before anything is counted.
///
/// Three metrics, because no one of them is honest on its own:
///   CER/WER      classic OCR accuracy, but punishes reordering harshly — and
///                a markdown table legitimately reorders a PDF's content stream.
///   Token F1     bag-of-words overlap; blind to order, so it survives the
///                reordering CER can't, and it's the closest proxy for what
///                search actually consumes (FTS tokens and embedded chunks).
///   Length ratio catches the two failure modes the others can mask: truncation
///                (num_predict cutoff) and repetition-loop padding.
/// </summary>
internal static class Scoring
{
    /// <summary>
    /// Levenshtein is O(n*m); a pathological page pair would otherwise dominate
    /// a run's wall clock. Both sides are cut to this before the edit distance
    /// (token metrics are unaffected), and <see cref="PageScore.Truncated"/>
    /// records that it happened so a report can't quietly present a partial
    /// comparison as a whole one.
    /// </summary>
    private const int MaxCharsForEditDistance = 12_000;

    private static readonly Regex MarkdownNoise = new(
        @"(?<img>!\[[^\]]*\]\([^)]*\))|(?<link>\[([^\]]*)\]\([^)]*\))|[#*_`~>|]+|^\s*[-=]{3,}\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex NonAlphanumeric = new(@"[^\p{L}\p{Nd}\s]+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Flatten to the comparable core: markdown stripped, links reduced to their
    /// text, unicode folded (NFKC turns curly quotes, ligatures and full-width
    /// forms into their plain equivalents), punctuation dropped, case folded,
    /// whitespace collapsed.
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Keep link text, drop the URL; drop image embeds entirely (mistral-ocr
        // emits ![img-0.jpeg](img-0.jpeg) placeholders that correspond to no
        // text in the reference).
        var s = MarkdownNoise.Replace(text, m =>
            m.Groups["img"].Success ? " " :
            m.Groups["link"].Success ? m.Groups[2].Value :
            " ");

        s = s.Normalize(NormalizationForm.FormKC);
        s = NonAlphanumeric.Replace(s, " ");
        s = Whitespace.Replace(s, " ").Trim();
        return s.ToLower(CultureInfo.InvariantCulture);
    }

    public static string[] Tokenize(string normalized) =>
        normalized.Length == 0 ? [] : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Edit distance with a two-row DP. Rows are int, so this allocates
    /// 2 * (m+1) * 4 bytes rather than the full n*m matrix.
    /// </summary>
    public static int EditDistance(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var ai = a[i - 1];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = ai == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Word-level edit distance — same DP over tokens instead of chars.</summary>
    public static int TokenEditDistance(string[] a, string[] b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// Multiset token overlap. Multiset rather than set: a reference with
    /// "total" three times and a hypothesis with it once is a real, partial
    /// miss, and set semantics would score it perfect.
    /// </summary>
    public static (double Precision, double Recall, double F1) TokenOverlap(string[] reference, string[] hypothesis)
    {
        if (reference.Length == 0 && hypothesis.Length == 0) return (1, 1, 1);
        if (reference.Length == 0 || hypothesis.Length == 0) return (0, 0, 0);

        var refCounts = new Dictionary<string, int>();
        foreach (var t in reference) refCounts[t] = refCounts.GetValueOrDefault(t) + 1;

        var matched = 0;
        foreach (var t in hypothesis)
        {
            if (refCounts.TryGetValue(t, out var n) && n > 0)
            {
                refCounts[t] = n - 1;
                matched++;
            }
        }

        var precision = (double)matched / hypothesis.Length;
        var recall = (double)matched / reference.Length;
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        return (precision, recall, f1);
    }

    public static PageScore Score(string reference, string hypothesis)
    {
        var refNorm = Normalize(reference);
        var hypNorm = Normalize(hypothesis);
        var refTokens = Tokenize(refNorm);
        var hypTokens = Tokenize(hypNorm);

        var truncated = refNorm.Length > MaxCharsForEditDistance || hypNorm.Length > MaxCharsForEditDistance;
        var refCut = refNorm.Length > MaxCharsForEditDistance ? refNorm[..MaxCharsForEditDistance] : refNorm;
        var hypCut = hypNorm.Length > MaxCharsForEditDistance ? hypNorm[..MaxCharsForEditDistance] : hypNorm;

        var cer = refCut.Length == 0 ? (hypCut.Length == 0 ? 0 : 1)
            : (double)EditDistance(refCut, hypCut) / refCut.Length;
        var wer = refTokens.Length == 0 ? (hypTokens.Length == 0 ? 0 : 1)
            : (double)TokenEditDistance(refTokens, hypTokens) / refTokens.Length;

        var (precision, recall, f1) = TokenOverlap(refTokens, hypTokens);

        return new PageScore(
            RefChars: refNorm.Length,
            HypChars: hypNorm.Length,
            RefTokens: refTokens.Length,
            HypTokens: hypTokens.Length,
            Cer: Math.Min(cer, 1.0),
            Wer: Math.Min(wer, 1.0),
            Precision: precision,
            Recall: recall,
            F1: f1,
            LengthRatio: refNorm.Length == 0 ? 0 : (double)hypNorm.Length / refNorm.Length,
            Truncated: truncated);
    }

    /// <summary>
    /// Engine-vs-engine agreement for the scans set, where no reference exists.
    /// Symmetric token F1: high agreement means both engines read the document
    /// the same way (weak evidence both are right), low agreement localises the
    /// pages worth reading by hand.
    /// </summary>
    public static double Agreement(string a, string b)
    {
        var (_, _, f1) = TokenOverlap(Tokenize(Normalize(a)), Tokenize(Normalize(b)));
        return f1;
    }
}

internal sealed record PageScore(
    int RefChars,
    int HypChars,
    int RefTokens,
    int HypTokens,
    double Cer,
    double Wer,
    double Precision,
    double Recall,
    double F1,
    double LengthRatio,
    bool Truncated);
