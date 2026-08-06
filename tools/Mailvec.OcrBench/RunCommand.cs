using System.Diagnostics;

namespace Mailvec.OcrBench;

/// <summary>
/// Runs one engine over a materialised sample and writes its raw output plus
/// per-call timings. Scoring is a separate command so an expensive run is
/// scored, re-scored, and compared without ever being repeated.
///
/// Calls are strictly sequential. Concurrency would make the latency numbers
/// meaningless (the local model serialises on one GPU anyway, and a remote
/// endpoint's per-call latency is exactly what we're measuring), and it is also
/// the only thing keeping a rate-limited endpoint from turning a bake-off into
/// a burst of 429s.
/// </summary>
internal static class RunCommand
{
    public static async Task<int> RunAsync(Args args, CancellationToken ct)
    {
        var workDir = args.Require("work");
        var engineName = args.Require("engine");
        var manifest = Json.Read<Manifest>(Path.Combine(workDir, "manifest.json"));

        using var engine = CreateEngine(engineName, args);
        var mode = args.Get("mode", engine is MistralOcrEngine ? "document" : "page");

        if (mode == "page" && !engine.SupportsPageMode)
            throw new ArgsException($"Engine '{engine.Name}' has no page mode.");
        if (mode == "document" && !engine.SupportsDocumentMode)
            throw new ArgsException($"Engine '{engine.Name}' has no document mode.");

        if (engine is OllamaEngine ollama && !await ollama.IsAvailableAsync(ct).ConfigureAwait(false))
            throw new ArgsException($"Vision model not available: {engine.Detail}. Pull it first.");

        var label = args.Get("label", $"{engine.Name}-{mode}");
        var outPath = Path.Combine(workDir, $"results-{label}.json");

        Console.Error.WriteLine($"Engine : {engine.Name} ({mode} mode) — {engine.Detail}");
        Console.Error.WriteLine($"Sample : {manifest.Documents.Count} documents, {manifest.TotalPages} pages [{manifest.Set}]");
        Console.Error.WriteLine($"Output : {outPath}");
        Console.Error.WriteLine();

        var runWatch = Stopwatch.StartNew();
        var documents = new List<DocumentResult>();

        foreach (var doc in manifest.Documents)
        {
            ct.ThrowIfCancellationRequested();
            var docWatch = Stopwatch.StartNew();
            try
            {
                var pages = mode == "document"
                    ? await RunDocumentAsync(engine, workDir, doc, ct).ConfigureAwait(false)
                    : await RunPagesAsync(engine, workDir, doc, ct).ConfigureAwait(false);

                docWatch.Stop();
                documents.Add(new DocumentResult(doc.AttachmentId, docWatch.ElapsedMilliseconds, pages));

                var chars = pages.Sum(p => p.Text.Length);
                var failed = pages.Count(p => p.Error is not null);
                Console.Error.WriteLine(
                    $"  a{doc.AttachmentId,-8} {pages.Count} page(s)  {docWatch.ElapsedMilliseconds,7} ms  {chars,7} chars" +
                    (failed > 0 ? $"  {failed} FAILED" : ""));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                docWatch.Stop();
                documents.Add(new DocumentResult(doc.AttachmentId, docWatch.ElapsedMilliseconds, [], Describe(ex)));
                Console.Error.WriteLine($"  a{doc.AttachmentId,-8} ERROR {Describe(ex)}");
            }
        }
        runWatch.Stop();

        Json.Write(outPath, new RunResult(
            engine.Name, engine.Detail, mode, DateTimeOffset.UtcNow.ToString("O"),
            runWatch.ElapsedMilliseconds, documents));

        var totalPages = documents.Sum(d => d.Pages.Count);
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            $"{totalPages} pages in {runWatch.Elapsed.TotalSeconds:F1}s " +
            $"({(totalPages == 0 ? 0 : runWatch.ElapsedMilliseconds / (double)totalPages / 1000):F1}s/page). Wrote {outPath}");
        return 0;
    }

    private static async Task<List<PageResult>> RunPagesAsync(
        IOcrEngine engine, string workDir, DocumentSample doc, CancellationToken ct)
    {
        var results = new List<PageResult>();
        foreach (var page in doc.Pages)
        {
            var jpeg = File.ReadAllBytes(Path.Combine(workDir, page.ImagePath));
            var watch = Stopwatch.StartNew();
            try
            {
                var text = await engine.OcrPageAsync(jpeg, ct).ConfigureAwait(false);
                watch.Stop();
                results.Add(new PageResult(
                    page.Index, ServiceMs(watch.ElapsedMilliseconds, engine.LastCallWaitMs), text));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                watch.Stop();
                // A per-page failure is recorded, not fatal — one unreadable page
                // shouldn't discard an expensive run's other results. The scorer
                // counts it as a miss and reports the count separately, so a run
                // riddled with errors can't read as a clean win.
                results.Add(new PageResult(page.Index, watch.ElapsedMilliseconds, string.Empty, Describe(ex)));
            }
        }
        return results;
    }

    /// <summary>
    /// Document mode: one call for the whole PDF. The service returns text for
    /// every page in the file, but the sample may only include some of them
    /// (max-pages, or truth-set pages that failed the reference threshold), so
    /// results are projected back onto the sampled page indices. Without that,
    /// page 0 of the response would be scored against sample page 4's reference.
    /// </summary>
    private static async Task<List<PageResult>> RunDocumentAsync(
        IOcrEngine engine, string workDir, DocumentSample doc, CancellationToken ct)
    {
        var pdf = File.ReadAllBytes(Path.Combine(workDir, doc.PdfPath));
        var watch = Stopwatch.StartNew();
        var pages = await engine.OcrDocumentAsync(pdf, ct).ConfigureAwait(false);
        watch.Stop();
        var serviceMs = ServiceMs(watch.ElapsedMilliseconds, engine.LastCallWaitMs);

        // Attribute the single call's cost evenly across the pages it covered —
        // per-page latency isn't separately observable in this mode, and
        // silently reporting the whole document's time as each page's would
        // inflate it by the page count.
        var perPageMs = pages.Count == 0 ? serviceMs : serviceMs / pages.Count;

        var results = new List<PageResult>();
        foreach (var page in doc.Pages)
        {
            results.Add(page.Index < pages.Count
                ? new PageResult(page.Index, perPageMs, pages[page.Index])
                : new PageResult(page.Index, perPageMs, string.Empty,
                    $"service returned {pages.Count} pages, sample expects index {page.Index}"));
        }
        return results;
    }

    /// <summary>
    /// Elapsed time minus the harness's own pacing and retry backoff, so the
    /// recorded per-call latency is the service's response time. Floored at 0
    /// against clock jitter. The run's wall clock is untouched and still shows
    /// what the throttled end-to-end cost actually was.
    /// </summary>
    private static long ServiceMs(long elapsedMs, long waitMs) => Math.Max(0, elapsedMs - waitMs);

    private static IOcrEngine CreateEngine(string name, Args args) => name.ToLowerInvariant() switch
    {
        "ollama" or "qwen" => new OllamaEngine(Config.Load().Ollama),
        "mistral" or "mistral-ocr" => CreateMistral(args),
        _ => throw new ArgsException($"Unknown engine '{name}'. Use 'ollama' or 'mistral'."),
    };

    private static MistralOcrEngine CreateMistral(Args args)
    {
        // Key from the environment only — never a flag (it would land in shell
        // history and in any `ps` output) and never a config file in the repo.
        var key = Environment.GetEnvironmentVariable("MISTRAL_OCR_KEY")
            ?? throw new ArgsException("Set MISTRAL_OCR_KEY in the environment (never pass the key as a flag).");
        var endpoint = args.GetOrNull("endpoint")
            ?? Environment.GetEnvironmentVariable("MISTRAL_OCR_ENDPOINT")
            ?? throw new ArgsException("Pass --endpoint or set MISTRAL_OCR_ENDPOINT.");

        return new MistralOcrEngine(
            endpoint,
            args.Get("route", "v1/ocr"),
            args.Get("model", "mistral-ocr-latest"),
            key,
            args.Get("auth-header", "bearer"),
            int.Parse(args.Get("timeout", "300")),
            // Default pacing is deliberately conservative. A benchmark that
            // trips the deployment's rate limit measures the quota, not the
            // engine — and does it in the flattering direction, since a
            // rejected call is fast. Lower it if the deployment has headroom.
            int.Parse(args.Get("min-interval-ms", "1500")),
            int.Parse(args.Get("max-retries", "5")));
    }

    /// <summary>
    /// Exception text for the results file. HttpRequestException carries the
    /// response body on Data["body"] rather than in the message (the request was
    /// a page of the user's mail, and an echoing error would put it in the log),
    /// so it's surfaced deliberately here — truncated, and only into the local
    /// results file the operator already has full access to.
    /// </summary>
    private static string Describe(Exception ex)
    {
        var body = ex.Data["body"] as string;
        return body is null ? $"{ex.GetType().Name}: {ex.Message}" : $"{ex.GetType().Name}: {ex.Message} | {body}";
    }
}
