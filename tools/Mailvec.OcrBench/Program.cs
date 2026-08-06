using Mailvec.OcrBench;

// Ctrl-C stops the current run cleanly rather than killing it mid-call — an
// expensive partial run is still worth the results it already has.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    var parsed = new Args(args);
    return parsed.Command switch
    {
        "sample" => await SampleCommand.RunAsync(parsed),
        "run" => await RunCommand.RunAsync(parsed, cts.Token),
        "score" => await ScoreCommand.RunAsync(parsed),
        _ => PrintHelp(),
    };
}
catch (ArgsException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.Error.WriteLine("cancelled.");
    return 130;
}

static int PrintHelp()
{
    Console.Error.WriteLine("""
        ocrbench — OCR engine bake-off over the real archive.

        A development tool. Read-only against the database and the Maildir;
        it never writes to the archive.

          sample --work DIR [--set truth|scans] [--n 40] [--max-pages 3] [--seed 1]
              Materialise a reproducible sample: PDFs, rendered pages, and (for
              the truth set) per-page reference text from the PDF's own text
              layer. Re-running with the same seed reproduces the same sample.

          run --work DIR --engine ollama|mistral [--mode page|document] [--label NAME]
              Run one engine over the sample and record its output and timings.
              mistral also needs --endpoint (or MISTRAL_OCR_ENDPOINT) and the
              MISTRAL_OCR_KEY environment variable; optionally --model,
              --route (default v1/ocr), --auth-header (bearer|api-key), --timeout.

          score --work DIR [--results a.json,b.json] [--corpus-pages N] [--cost-per-1k 1.0]
              Score every run in the working directory and write report.md.

        Typical bake-off:
          ocrbench sample --work ~/ocr-bench/truth --set truth  --n 40
          ocrbench sample --work ~/ocr-bench/scans --set scans --n 25
          ocrbench run   --work ~/ocr-bench/truth --engine ollama
          ocrbench run   --work ~/ocr-bench/truth --engine mistral --mode page
          ocrbench run   --work ~/ocr-bench/truth --engine mistral --mode document
          ocrbench score --work ~/ocr-bench/truth --corpus-pages 3000
        """);
    return 1;
}
