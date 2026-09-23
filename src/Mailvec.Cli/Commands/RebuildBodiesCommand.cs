using System.CommandLine;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Microsoft.Extensions.Options;
using Mailvec.Parsing.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Re-derives <c>body_text</c> for every message that has stored HTML by
/// running the HTML-to-text converter over <c>body_html</c>. Used after
/// changing the converter without forcing a full Maildir re-scan. FTS5
/// triggers on <c>messages</c> keep the index in sync automatically.
/// </summary>
/// <remarks>
/// <para><b>Dry run by default</b>, like <c>reocr</c>: it converts every row
/// and reports how many bodies would actually change, and writes only with
/// <c>--apply</c>. It used to write immediately, and with <c>--reembed</c> it
/// re-queued the ENTIRE corpus for embedding whether or not a row's text
/// moved — weeks of work on a CPU-only embedding host, from one mistyped
/// command.</para>
///
/// <para><b>Only changed rows are written</b>, and under <c>--reembed</c> only
/// those rows are re-queued and have their chunks dropped — in the same
/// transaction as the body write, so a rewritten body never sits beside
/// vectors built from the old one.</para>
///
/// <para><b>Each write is guarded by the snapshot it was derived from</b>: the
/// UPDATE matches only while the row's <c>body_html</c> and <c>subject</c> are
/// still the values that were converted. The conversion happens outside any
/// transaction (it crosses to the parse service), so without the guard an
/// indexer upsert landing mid-run was overwritten with text converted from
/// the OLD HTML — FTS disagreeing with the stored HTML and the vectors, with
/// nothing to re-trigger a fix. The same rule <c>extract-attachments</c>
/// follows. A skipped row is reported; a re-run picks it up.</para>
///
/// <para><b>Streamed</b> in id order, one batch at a time, rather than loading
/// every HTML body up front (hundreds of MB on a real archive, inside mcp's
/// container when run through <c>docker compose exec</c>).</para>
/// </remarks>
internal static class RebuildBodiesCommand
{
    public static Command Build()
    {
        var reembedOpt = new Option<bool>("--reembed") { Description = "Also re-queue each CHANGED message for embedding (and drop its stale vectors) in the same transaction as its body rewrite." };
        var applyOpt = new Option<bool>("--apply") { Description = "Write the changes. Without it this is a dry run: every body is converted and the number that would change is reported, but nothing is written." };

        var cmd = new Command("rebuild-bodies", "Re-run HTML-to-text on stored body_html and overwrite body_text where it changed (dry run unless --apply).")
        {
            reembedOpt,
            applyOpt,
        };

        cmd.SetAction(parse => Run(parse.GetValue(reembedOpt), parse.GetValue(applyOpt)));
        return cmd;
    }

    private static int Run(bool reembed, bool apply)
    {
        using var sp = CliServices.Build();
        return Execute(sp, reembed, Console.Out, Console.Error, apply);
    }

    private const int BatchSize = 500;

    private sealed record Row(long Id, string? Subject, string Html, string? BodyText);

    /// <summary>Test seam — see <see cref="PurgeDeletedCommand"/> for the pattern.</summary>
    internal static int Execute(IServiceProvider sp, bool reembed, TextWriter @out, TextWriter err, bool apply)
    {
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        // This command re-selects every row each run, so a stop on the parse
        // host's routine recycle meant a large archive could never finish.
        var parser = RetryOnUnavailable.Wrap(
            sp.GetRequiredService<IMailParser>(), sp.GetRequiredService<IOptions<ParserOptions>>().Value, err);
        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();

        long total;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM messages WHERE body_html IS NOT NULL";
            total = Convert.ToInt64(countCmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        if (total == 0)
        {
            @out.WriteLine("No messages with body_html. Nothing to do.");
            return 0;
        }

        @out.WriteLine(apply
            ? $"Re-deriving body_text for {total:N0} messages..."
            : $"DRY RUN: converting {total:N0} messages to see which bodies would change (nothing is written)...");

        long examined = 0, changed = 0, written = 0, skippedStale = 0, errors = 0;
        long lastId = 0;
        var parserUnavailable = false;

        while (!parserUnavailable)
        {
            var batch = FetchBatch(conn, lastId);
            if (batch.Count == 0) break;
            lastId = batch[^1].Id;

            // Convert with no transaction open: this crosses to the parse
            // service, and a transaction held across it would pin SQLite's
            // writer slot (the extract-attachments trap).
            var converted = new List<(Row Row, string? Text)>(batch.Count);
            foreach (var row in batch)
            {
                try
                {
                    var newText = parser.BodyTextFromHtml(row.Html, row.Subject);
                    converted.Add((row, string.IsNullOrEmpty(newText) ? null : newText));
                }
                catch (ParseException ex) when (ex.Kind == ParseFailureKind.Unavailable)
                {
                    // Not this row's fault, and every remaining row would fail
                    // the same way: commit what this batch converted so far and
                    // stop, rather than logging one "error" per message left.
                    err.WriteLine($"  id={row.Id}: the parse service is unavailable ({ex.Message}); stopping after this batch.");
                    parserUnavailable = true;
                    break;
                }
                catch (Exception ex)
                {
                    err.WriteLine($"  id={row.Id}: convert failed ({ex.GetType().Name}: {ex.Message})");
                    errors++;
                }
            }

            examined += converted.Count;
            var toWrite = converted
                .Where(c => !string.Equals(c.Text, string.IsNullOrEmpty(c.Row.BodyText) ? null : c.Row.BodyText, StringComparison.Ordinal))
                .ToList();
            changed += toWrite.Count;

            if (apply && toWrite.Count > 0)
            {
                var (w, s) = WriteBatch(conn, toWrite, reembed);
                written += w;
                skippedStale += s;
            }

            if (examined < total) @out.WriteLine($"  ... {examined:N0}/{total:N0} examined, {changed:N0} changed");
        }

        if (!apply)
        {
            @out.WriteLine($"DRY RUN: {changed:N0} of {examined:N0} bodies would change ({errors:N0} conversion errors).");
            if (reembed)
                @out.WriteLine($"With --reembed, those {changed:N0} messages would be re-queued for embedding; the other {examined - changed:N0} keep their vectors.");
            @out.WriteLine("Re-run with --apply to write.");
            if (parserUnavailable)
            {
                @out.WriteLine("STOPPED: the parse service is unavailable, so the count above covers only the rows examined.");
                return 1;
            }
            return errors == 0 ? 0 : 1;
        }

        @out.WriteLine($"Updated body_text on {written:N0} messages ({errors:N0} errors); {examined - changed:N0} already matched and were left alone.");
        if (skippedStale > 0)
            @out.WriteLine($"SKIPPED {skippedStale:N0}: body_html or subject changed after the row was read (an indexer update mid-run); re-run to convert them against their new HTML.");

        if (parserUnavailable)
        {
            // Every written row was committed (and, under --reembed,
            // re-queued) in its own batch transaction, so stopping loses
            // nothing already done.
            @out.WriteLine("STOPPED: the parse service is unavailable. Re-run once it is back (`docker compose ps parse`) to convert the rest.");
            return 1;
        }

        if (reembed)
        {
            @out.WriteLine($"Re-queued {written:N0} changed messages for embedding and dropped their stale vectors. Run the embedder to regenerate.");
        }
        else
        {
            @out.WriteLine("FTS5 is updated automatically via triggers. Embeddings still reflect the OLD body_text for the rewritten messages.");
            @out.WriteLine("Rerun with --reembed --apply (it re-queues only bodies that change) when you're ready to refresh vectors.");
        }
        return errors == 0 ? 0 : 1;
    }

    private static List<Row> FetchBatch(Microsoft.Data.Sqlite.SqliteConnection conn, long afterId)
    {
        using var fetch = conn.CreateCommand();
        fetch.CommandText = """
            SELECT id, subject, body_html, body_text FROM messages
            WHERE body_html IS NOT NULL AND id > $after
            ORDER BY id
            LIMIT $limit
            """;
        fetch.Parameters.AddWithValue("$after", afterId);
        fetch.Parameters.AddWithValue("$limit", BatchSize);
        var rows = new List<Row>(BatchSize);
        using var r = fetch.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new Row(
                r.GetInt64(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return rows;
    }

    /// <summary>
    /// Write one batch of changed bodies in one short transaction (a few
    /// hundred UPDATEs — milliseconds of writer lock). Returns (written,
    /// skipped because the snapshot moved).
    /// </summary>
    private static (long Written, long Stale) WriteBatch(
        Microsoft.Data.Sqlite.SqliteConnection conn, List<(Row Row, string? Text)> rows, bool reembed)
    {
        long written = 0, stale = 0;
        using var tx = conn.BeginTransaction();
        using var update = conn.CreateCommand();
        update.Transaction = tx;
        // The snapshot guard: only while body_html and subject are exactly
        // what was converted. Under --reembed the re-queue rides in the SAME
        // statement, and embed_epoch moves too: body_text changes here without
        // content_hash changing (the hash covers MimeMessage.Body bytes, not
        // the converter output), so the embedder's hash guard alone would let
        // an in-flight embed stamp over the re-queue.
        update.CommandText = (reembed
            ? "UPDATE messages SET body_text = $body, embedded_at = NULL, embed_epoch = embed_epoch + 1"
            : "UPDATE messages SET body_text = $body")
            + " WHERE id = $id AND body_html IS $html AND subject IS $subject";
        var bodyParam = update.Parameters.Add("$body", Microsoft.Data.Sqlite.SqliteType.Text);
        var idParam = update.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        var htmlParam = update.Parameters.Add("$html", Microsoft.Data.Sqlite.SqliteType.Text);
        var subjectParam = update.Parameters.Add("$subject", Microsoft.Data.Sqlite.SqliteType.Text);

        foreach (var (row, text) in rows)
        {
            bodyParam.Value = (object?)text ?? DBNull.Value;
            idParam.Value = row.Id;
            htmlParam.Value = row.Html;
            subjectParam.Value = (object?)row.Subject ?? DBNull.Value;
            if (update.ExecuteNonQuery() == 0)
            {
                stale++;
                continue;
            }
            written++;
            // Drop the vectors built from the old text atomically with the
            // rewrite, rather than one corpus-wide clear at the end.
            if (reembed) ChunkRepository.DeleteChunksForMessage(conn, tx, row.Id);
        }
        tx.Commit();
        return (written, stale);
    }
}
