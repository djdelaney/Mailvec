using System.Globalization;
using System.Text;

namespace Mailvec.OcrBench;

/// <summary>
/// Scores one or more runs against the sample and writes a markdown report.
///
/// Two report shapes, because the two sample sets answer different questions:
///   truth — per-engine CER/WER/F1 against PdfPig's text layer, plus latency.
///   scans — no reference exists, so: pairwise agreement between engines,
///           output length, latency, and a worked list of the pages where the
///           engines disagree most (the ones worth reading by hand).
/// </summary>
internal static class ScoreCommand
{
    public static Task<int> RunAsync(Args args)
    {
        var workDir = args.Require("work");
        var manifest = Json.Read<Manifest>(Path.Combine(workDir, "manifest.json"));

        var runPaths = args.GetMany("results");
        if (runPaths.Count == 0)
            runPaths = [.. Directory.GetFiles(workDir, "results-*.json").Select(Path.GetFileName)!];
        if (runPaths.Count == 0) throw new ArgsException("No results-*.json found. Run at least one engine first.");

        var runs = runPaths
            .Select(p => Json.Read<RunResult>(Path.IsPathRooted(p) ? p : Path.Combine(workDir, p)))
            .ToList();
        var labels = runPaths.Select(LabelOf).ToList();

        var report = new StringBuilder();
        report.AppendLine("# OCR bake-off");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Sample: **{manifest.Set}** — {manifest.Documents.Count} documents, {manifest.TotalPages} pages, sampled {manifest.CreatedUtc}.");
        report.AppendLine(CultureInfo.InvariantCulture, $"Render: {manifest.Renderer.Dpi} DPI, long edge ≤ {manifest.Renderer.MaxEdgePx}px, JPEG q{manifest.Renderer.JpegQuality}.");
        report.AppendLine();

        report.AppendLine("## Engines");
        report.AppendLine();
        report.AppendLine("| label | engine | mode | detail |");
        report.AppendLine("|---|---|---|---|");
        for (var i = 0; i < runs.Count; i++)
            report.AppendLine(CultureInfo.InvariantCulture, $"| `{labels[i]}` | {runs[i].Engine} | {runs[i].Mode} | {runs[i].EngineDetail} |");
        report.AppendLine();

        if (manifest.Set == SampleSet.Truth)
            AppendTruthReport(report, manifest, runs, labels, workDir);
        else
            AppendScansReport(report, manifest, runs, labels);

        AppendLatency(report, runs, labels);

        var corpusPages = int.Parse(args.Get("corpus-pages", "0"), CultureInfo.InvariantCulture);
        if (corpusPages > 0)
            AppendProjection(report, runs, labels, corpusPages,
                double.Parse(args.Get("cost-per-1k", "1.0"), CultureInfo.InvariantCulture));

        var outPath = Path.Combine(workDir, args.Get("out", "report.md"));
        File.WriteAllText(outPath, report.ToString());
        Console.Error.WriteLine($"Wrote {outPath}");
        Console.WriteLine(report.ToString());
        return Task.FromResult(0);
    }

    private static string LabelOf(string path) =>
        Path.GetFileNameWithoutExtension(path).Replace("results-", "", StringComparison.Ordinal);

    private static void AppendTruthReport(
        StringBuilder report, Manifest manifest, List<RunResult> runs, List<string> labels, string workDir)
    {
        report.AppendLine("## Accuracy vs the PDF text layer");
        report.AppendLine();
        report.AppendLine("Reference is PdfPig's `ContentOrderTextExtractor` output — the same extractor the indexer uses, on PDFs that carry a real text layer. Both sides are normalised (markdown stripped, NFKC, punctuation and case folded, whitespace collapsed) before scoring, so this measures transcription rather than formatting.");
        report.AppendLine();
        report.AppendLine("**These are clean, born-digital pages. This is a ceiling, not a measure of robustness to real scan degradation — see the `scans` set for that.**");
        report.AppendLine();
        report.AppendLine("| label | coverage | scored | CER ↓ | WER ↓ | token F1 ↑ | recall ↑ | precision ↑ | len ratio | empty |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        var lowCoverage = new List<string>();

        foreach (var (run, label) in runs.Zip(labels))
        {
            var scores = new List<PageScore>();
            var errors = 0;
            var empty = 0;
            var total = 0;

            foreach (var doc in manifest.Documents)
            {
                var result = run.Documents.FirstOrDefault(d => d.AttachmentId == doc.AttachmentId);
                foreach (var page in doc.Pages)
                {
                    if (page.ReferencePath is null) continue;
                    total++;
                    var pageResult = result?.Pages.FirstOrDefault(p => p.Index == page.Index);

                    // A transport failure — a rate limit, a timeout, a dropped
                    // connection — says nothing about how well the engine reads
                    // a page. Scoring it as empty output would charge a quota
                    // problem to the model's accuracy, and on a throttled
                    // deployment that buries a good engine. Excluded from the
                    // quality metrics and surfaced as COVERAGE instead, which is
                    // the honest place for it: a run that only answered a third
                    // of the sample must not read as a clean comparison.
                    if (result?.Error is not null || pageResult?.Error is not null) { errors++; continue; }

                    // No error but no text IS a transcription failure — the
                    // engine answered and had nothing. That stays scored.
                    var hypothesis = pageResult?.Text ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(hypothesis)) empty++;

                    var reference = File.ReadAllText(Path.Combine(workDir, page.ReferencePath));
                    scores.Add(Scoring.Score(reference, hypothesis));
                }
            }

            if (total == 0) continue;
            var coverage = (double)scores.Count / total;
            if (coverage < 0.9) lowCoverage.Add($"`{label}` ({scores.Count}/{total} pages, {errors} transport failures)");

            if (scores.Count == 0)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"| `{label}` | 0% | 0 | — | — | — | — | — | — | — |");
                continue;
            }

            report.AppendLine(CultureInfo.InvariantCulture,
                $"| `{label}` | {coverage:P0} | {scores.Count} | {scores.Average(s => s.Cer):F3} | {scores.Average(s => s.Wer):F3} | " +
                $"{scores.Average(s => s.F1):F3} | {scores.Average(s => s.Recall):F3} | {scores.Average(s => s.Precision):F3} | " +
                $"{scores.Average(s => s.LengthRatio):F2} | {empty} |");
        }
        report.AppendLine();
        report.AppendLine("`coverage` is the share of sampled pages the engine actually returned. Pages lost to transport failures (rate limits, timeouts) are excluded from the quality columns — they measure the deployment, not the model. Pages the engine answered with nothing ARE scored, under `empty`.");
        report.AppendLine();
        report.AppendLine("`len ratio` is hypothesis length over reference length: well under 1.0 means truncation (check the output-token cap), well over means padding or a repetition loop.");
        report.AppendLine();

        if (lowCoverage.Count > 0)
        {
            report.AppendLine("> ⚠️ **Incomplete coverage — do not compare these rows as equals:** " + string.Join("; ", lowCoverage) + ".");
            report.AppendLine("> Their quality figures come from whichever pages happened to get through. Re-run before drawing a conclusion.");
            report.AppendLine();
        }
    }

    private static void AppendScansReport(
        StringBuilder report, Manifest manifest, List<RunResult> runs, List<string> labels)
    {
        report.AppendLine("## Real scans — no reference available");
        report.AppendLine();
        report.AppendLine("These PDFs are image-only: there is no text layer, so there is nothing to score against. What follows is engine-vs-engine agreement plus output volume. **High agreement is weak evidence both engines are right; low agreement only says they differ.** The disagreement table at the end is the read-by-hand list.");
        report.AppendLine();

        report.AppendLine("| label | pages | mean chars/page | empty pages | errors |");
        report.AppendLine("|---|---|---|---|---|");
        foreach (var (run, label) in runs.Zip(labels))
        {
            var pages = run.Documents.SelectMany(d => d.Pages).ToList();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| `{label}` | {pages.Count} | {(pages.Count == 0 ? 0 : pages.Average(p => p.Text.Length)):F0} | " +
                $"{pages.Count(p => string.IsNullOrWhiteSpace(p.Text))} | " +
                $"{pages.Count(p => p.Error is not null) + run.Documents.Count(d => d.Error is not null)} |");
        }
        report.AppendLine();

        if (runs.Count < 2) return;

        report.AppendLine("### Pairwise agreement (token F1)");
        report.AppendLine();
        var disagreements = new List<(string Doc, int Page, string Pair, double Agreement)>();

        for (var i = 0; i < runs.Count; i++)
        {
            for (var j = i + 1; j < runs.Count; j++)
            {
                var pairScores = new List<double>();
                foreach (var doc in manifest.Documents)
                {
                    var a = runs[i].Documents.FirstOrDefault(d => d.AttachmentId == doc.AttachmentId);
                    var b = runs[j].Documents.FirstOrDefault(d => d.AttachmentId == doc.AttachmentId);
                    foreach (var page in doc.Pages)
                    {
                        var ta = a?.Pages.FirstOrDefault(p => p.Index == page.Index)?.Text ?? string.Empty;
                        var tb = b?.Pages.FirstOrDefault(p => p.Index == page.Index)?.Text ?? string.Empty;
                        var agreement = Scoring.Agreement(ta, tb);
                        pairScores.Add(agreement);
                        disagreements.Add(($"a{doc.AttachmentId}", page.Index, $"{labels[i]} vs {labels[j]}", agreement));
                    }
                }
                if (pairScores.Count > 0)
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"- **{labels[i]}** vs **{labels[j]}**: {pairScores.Average():F3} mean over {pairScores.Count} pages");
            }
        }
        report.AppendLine();

        report.AppendLine("### Lowest-agreement pages (inspect these by hand)");
        report.AppendLine();
        report.AppendLine("| document | page | pair | agreement |");
        report.AppendLine("|---|---|---|---|");
        foreach (var d in disagreements.OrderBy(d => d.Agreement).Take(15))
            report.AppendLine(CultureInfo.InvariantCulture, $"| {d.Doc} | {d.Page} | {d.Pair} | {d.Agreement:F3} |");
        report.AppendLine();
    }

    private static void AppendLatency(
        StringBuilder report, List<RunResult> runs, List<string> labels)
    {
        report.AppendLine("## Latency and cost");
        report.AppendLine();
        report.AppendLine("| label | wall clock | timed pages | mean s/page | median s/page | p95 s/page |");
        report.AppendLine("|---|---|---|---|---|---|");

        foreach (var (run, label) in runs.Zip(labels))
        {
            // Failed calls are excluded here for the same reason they're excluded
            // from the quality columns, and it matters MORE here: a rate-limited
            // request is rejected in milliseconds, so including rejections drags
            // the mean toward zero and makes a throttled engine look fastest.
            // Observed: a run with 60 of 71 pages 429'd reported 0.9 s/page.
            var perPage = run.Documents
                .SelectMany(d => d.Pages)
                .Where(p => p.Error is null)
                .Select(p => p.ElapsedMs / 1000.0)
                .OrderBy(x => x)
                .ToList();
            if (perPage.Count == 0) continue;
            var median = perPage[perPage.Count / 2];
            var p95 = perPage[(int)Math.Min(perPage.Count - 1, Math.Floor(perPage.Count * 0.95))];

            report.AppendLine(CultureInfo.InvariantCulture,
                $"| `{label}` | {TimeSpan.FromMilliseconds(run.WallClockMs):hh\\:mm\\:ss} | {perPage.Count} | {perPage.Average():F1} | " +
                $"{median:F1} | {p95:F1} |");
        }
        report.AppendLine();
        report.AppendLine("Per-page figures cover successful calls only, and exclude the harness's own pacing and retry backoff — they are service response time. `wall clock` is the unfiltered end-to-end cost, throttling included; compare `timed pages` against the sample size before trusting a mean.");
        report.AppendLine();
        report.AppendLine("In `document` mode per-page latency is the document call divided by its page count — the call is not separately observable per page, so treat those figures as an average, not a measurement.");
        report.AppendLine();
    }

    /// <summary>
    /// Scales the sample's measured per-page latency to a whole-corpus backlog.
    /// The sample is a few dozen pages; the decision is about thousands, and the
    /// two engines' costs scale differently (GPU hours vs per-page billing).
    /// Both numbers are extrapolations from a small sample — stated as such.
    /// </summary>
    private static void AppendProjection(
        StringBuilder report, List<RunResult> runs, List<string> labels, int corpusPages, double costPerThousand)
    {
        report.AppendLine("## Corpus projection");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Extrapolated from this sample's mean page latency to **{corpusPages} pages**. Linear extrapolation from a few dozen pages — indicative, not a forecast.");
        report.AppendLine();
        report.AppendLine("| label | projected wall clock | projected API cost |");
        report.AppendLine("|---|---|---|");

        foreach (var (run, label) in runs.Zip(labels))
        {
            var perPage = run.Documents.SelectMany(d => d.Pages).Select(p => p.ElapsedMs / 1000.0).ToList();
            if (perPage.Count == 0) continue;
            var projected = TimeSpan.FromSeconds(perPage.Average() * corpusPages);
            // The local engine bills nothing per page; only the remote one does.
            var cost = run.Engine == "mistral-ocr"
                ? $"${corpusPages / 1000.0 * costPerThousand:F2}"
                : "— (local)";
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| `{label}` | {projected:d\\d\\ hh\\:mm\\:ss} | {cost} |");
        }
        report.AppendLine();
    }
}
