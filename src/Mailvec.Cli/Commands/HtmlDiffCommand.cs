using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Web;
using Mailvec.Core.Data;
using Mailvec.Core.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Pulls a random sample of messages with HTML bodies, runs both
/// <see cref="HtmlToTextV1"/> and <see cref="HtmlToTextV2"/> against each,
/// and emits a self-contained HTML report with the two outputs side-by-side
/// plus byte/word/line deltas. Used to evaluate whether the V2 (AngleSharp)
/// converter actually reduces marketing-email noise on a real corpus.
/// </summary>
internal static class HtmlDiffCommand
{
    public static Command Build()
    {
        var sampleOpt = new Option<int>("--sample") { DefaultValueFactory = _ => 100, Description = "Number of messages to sample." };
        var seedOpt = new Option<int?>("--seed") { Description = "Random seed for reproducible sampling." };
        var outputOpt = new Option<string>("--output") { DefaultValueFactory = _ => "./html-diff.html", Description = "Path to write the HTML report." };
        var minBytesOpt = new Option<int>("--min-bytes") { DefaultValueFactory = _ => 500, Description = "Skip messages with body_html shorter than this." };

        var cmd = new Command("html-diff", "Compare HtmlToTextV1 vs V2 output across a random sample of messages.")
        {
            sampleOpt,
            seedOpt,
            outputOpt,
            minBytesOpt,
        };

        cmd.SetAction(parseResult => Run(
            parseResult.GetValue(sampleOpt),
            parseResult.GetValue(seedOpt),
            parseResult.GetValue(outputOpt) ?? "./html-diff.html",
            parseResult.GetValue(minBytesOpt)));
        return cmd;
    }

    private static int Run(int sample, int? seed, string outputPath, int minBytes)
    {
        using var sp = CliServices.Build();
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();

        var ids = new List<long>();
        using (var idCmd = conn.CreateCommand())
        {
            idCmd.CommandText = "SELECT id FROM messages WHERE body_html IS NOT NULL AND length(body_html) >= $min";
            idCmd.Parameters.AddWithValue("$min", minBytes);
            using var r = idCmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }

        if (ids.Count == 0)
        {
            Console.Error.WriteLine($"No messages with body_html >= {minBytes} bytes found. Database may be empty.");
            return 1;
        }

        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        Shuffle(ids, rng);
        var pick = ids.Take(Math.Min(sample, ids.Count)).ToList();

        Console.WriteLine($"Sampling {pick.Count} of {ids.Count:N0} HTML messages (>= {minBytes} bytes).");

        var rows = new List<DiffRow>(pick.Count);
        long totalV1Bytes = 0, totalV2Bytes = 0;
        long totalV1Ms = 0, totalV2Ms = 0;

        using (var fetch = conn.CreateCommand())
        {
            fetch.CommandText = "SELECT id, subject, from_address, from_name, date_sent, body_html FROM messages WHERE id = $id";
            var idParam = fetch.CreateParameter();
            idParam.ParameterName = "$id";
            fetch.Parameters.Add(idParam);

            foreach (var id in pick)
            {
                idParam.Value = id;
                using var r = fetch.ExecuteReader();
                if (!r.Read()) continue;

                var subject = r.IsDBNull(1) ? null : r.GetString(1);
                var fromAddr = r.IsDBNull(2) ? null : r.GetString(2);
                var fromName = r.IsDBNull(3) ? null : r.GetString(3);
                var dateSent = r.IsDBNull(4) ? null : r.GetString(4);
                var html = r.GetString(5);

                var sw1 = Stopwatch.StartNew();
                string v1;
                string? v1Err = null;
                try { v1 = HtmlToTextV1.Convert(html); }
                catch (Exception ex) { v1 = string.Empty; v1Err = ex.Message; }
                sw1.Stop();

                var sw2 = Stopwatch.StartNew();
                string v2;
                string? v2Err = null;
                try { v2 = HtmlToTextV2.Convert(html); }
                catch (Exception ex) { v2 = string.Empty; v2Err = ex.Message; }
                sw2.Stop();

                totalV1Bytes += v1.Length;
                totalV2Bytes += v2.Length;
                totalV1Ms += sw1.ElapsedMilliseconds;
                totalV2Ms += sw2.ElapsedMilliseconds;

                rows.Add(new DiffRow(
                    Id: id,
                    Subject: subject,
                    FromAddress: fromAddr,
                    FromName: fromName,
                    DateSent: dateSent,
                    HtmlBytes: html.Length,
                    V1: v1,
                    V2: v2,
                    V1Ms: sw1.ElapsedMilliseconds,
                    V2Ms: sw2.ElapsedMilliseconds,
                    V1Error: v1Err,
                    V2Error: v2Err));
            }
        }

        WriteReport(outputPath, rows, totalV1Bytes, totalV2Bytes, totalV1Ms, totalV2Ms);

        var deltaPct = totalV1Bytes == 0 ? 0 : (totalV2Bytes - totalV1Bytes) * 100.0 / totalV1Bytes;
        Console.WriteLine($"V1 total bytes: {totalV1Bytes:N0}   ({totalV1Ms} ms total)");
        Console.WriteLine($"V2 total bytes: {totalV2Bytes:N0}   ({totalV2Ms} ms total)");
        Console.WriteLine($"V2 vs V1 size: {deltaPct:+0.0;-0.0;0}%");
        Console.WriteLine($"Wrote report: {Path.GetFullPath(outputPath)}");
        return 0;
    }

    private static void Shuffle<T>(List<T> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static void WriteReport(string path, List<DiffRow> rows, long v1Bytes, long v2Bytes, long v1Ms, long v2Ms)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>HTML-to-text V1 vs V2</title>
            <style>
              body { font-family: -apple-system, system-ui, sans-serif; margin: 0; padding: 20px; background: #fafafa; color: #222; }
              h1 { margin: 0 0 8px 0; font-size: 18px; }
              .summary { background: #fff; padding: 12px 16px; border: 1px solid #ddd; border-radius: 6px; margin-bottom: 20px; }
              .summary code { background: #f0f0f0; padding: 1px 4px; border-radius: 3px; }
              .row { background: #fff; border: 1px solid #ddd; border-radius: 6px; margin-bottom: 16px; overflow: hidden; }
              .header { background: #f4f4f4; padding: 8px 12px; border-bottom: 1px solid #ddd; font-size: 12px; }
              .header .subj { font-weight: 600; font-size: 14px; }
              .header .meta { color: #666; margin-top: 2px; }
              .stats { color: #444; margin-top: 4px; font-family: ui-monospace, Menlo, monospace; font-size: 11px; }
              .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 0; }
              .col { padding: 10px 12px; font-family: ui-monospace, Menlo, monospace; font-size: 11px; white-space: pre-wrap; word-break: break-word; max-height: 400px; overflow-y: auto; }
              .col.v1 { background: #fff7f0; border-right: 1px solid #ddd; }
              .col.v2 { background: #f0f7ff; }
              .col h3 { margin: 0 0 6px 0; font-family: -apple-system, system-ui, sans-serif; font-size: 11px; text-transform: uppercase; color: #888; letter-spacing: 0.04em; }
              .err { color: #c00; }
              .smaller { color: #060; }
              .larger { color: #c60; }
            </style></head><body>
            """);

        var deltaBytes = v2Bytes - v1Bytes;
        var deltaPct = v1Bytes == 0 ? 0 : deltaBytes * 100.0 / v1Bytes;
        sb.Append("<h1>HTML-to-text V1 vs V2</h1>");
        sb.Append("<div class=\"summary\">");
        sb.AppendFormat(CultureInfo.InvariantCulture, "<b>Sample:</b> {0} messages<br>", rows.Count);
        sb.AppendFormat(CultureInfo.InvariantCulture, "<b>V1 total:</b> {0:N0} bytes, {1} ms<br>", v1Bytes, v1Ms);
        sb.AppendFormat(CultureInfo.InvariantCulture, "<b>V2 total:</b> {0:N0} bytes, {1} ms<br>", v2Bytes, v2Ms);
        sb.AppendFormat(CultureInfo.InvariantCulture, "<b>V2 vs V1:</b> {0:+#,0;-#,0;0} bytes ({1:+0.0;-0.0;0}%)", deltaBytes, deltaPct);
        sb.Append("</div>");

        foreach (var row in rows)
        {
            var v1Len = row.V1.Length;
            var v2Len = row.V2.Length;
            var rowDelta = v2Len - v1Len;
            var rowDeltaPct = v1Len == 0 ? 0 : rowDelta * 100.0 / v1Len;
            var deltaCls = rowDelta < 0 ? "smaller" : (rowDelta > 0 ? "larger" : "");

            sb.Append("<div class=\"row\">");
            sb.Append("<div class=\"header\">");
            sb.AppendFormat(CultureInfo.InvariantCulture, "<div class=\"subj\">{0}</div>", Esc(row.Subject ?? "(no subject)"));
            sb.AppendFormat(CultureInfo.InvariantCulture, "<div class=\"meta\">id={0} &bull; from: {1} &lt;{2}&gt; &bull; sent: {3}</div>",
                row.Id,
                Esc(row.FromName ?? "?"),
                Esc(row.FromAddress ?? "?"),
                Esc(row.DateSent ?? "?"));
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "<div class=\"stats\">html={0:N0}b &bull; v1={1:N0}b ({2}ms) &bull; v2={3:N0}b ({4}ms) &bull; <span class=\"{5}\">delta={6:+#,0;-#,0;0}b ({7:+0.0;-0.0;0}%)</span></div>",
                row.HtmlBytes, v1Len, row.V1Ms, v2Len, row.V2Ms, deltaCls, rowDelta, rowDeltaPct);
            sb.Append("</div>");

            sb.Append("<div class=\"grid\">");
            sb.Append("<div class=\"col v1\"><h3>V1 (current — HtmlTokenizer)</h3>");
            if (row.V1Error is not null) sb.AppendFormat("<div class=\"err\">ERROR: {0}</div>", Esc(row.V1Error));
            sb.Append(Esc(row.V1));
            sb.Append("</div>");
            sb.Append("<div class=\"col v2\"><h3>V2 (proposed — AngleSharp)</h3>");
            if (row.V2Error is not null) sb.AppendFormat("<div class=\"err\">ERROR: {0}</div>", Esc(row.V2Error));
            sb.Append(Esc(row.V2));
            sb.Append("</div>");
            sb.Append("</div>");

            sb.Append("</div>");
        }

        sb.Append("</body></html>");

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString());
    }

    private static string Esc(string s) => HttpUtility.HtmlEncode(s);

    private sealed record DiffRow(
        long Id,
        string? Subject,
        string? FromAddress,
        string? FromName,
        string? DateSent,
        int HtmlBytes,
        string V1,
        string V2,
        long V1Ms,
        long V2Ms,
        string? V1Error,
        string? V2Error);
}
