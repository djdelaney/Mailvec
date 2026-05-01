using System.CommandLine;
using Mailvec.Core.Data;
using Mailvec.Core.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Re-derives <c>body_text</c> for every message that has stored HTML by
/// running <see cref="HtmlToText"/> over <c>body_html</c>. Used after
/// changing the HTML-to-text converter without forcing a full Maildir
/// re-scan. FTS5 triggers on <c>messages</c> keep the index in sync
/// automatically. Embeddings reflect the OLD body_text and are stale
/// after this runs — pass <c>--reembed</c> (or run <c>mailvec reindex --all</c>
/// separately) to flag them for re-embedding.
/// </summary>
internal static class RebuildBodiesCommand
{
    public static Command Build()
    {
        var reembedOpt = new Option<bool>("--reembed") { Description = "Also clear embedded_at so the embedder will regenerate vectors against the new body_text." };

        var cmd = new Command("rebuild-bodies", "Re-run HTML-to-text on stored body_html and overwrite body_text in place.")
        {
            reembedOpt,
        };

        cmd.SetAction(parse => Run(parse.GetValue(reembedOpt)));
        return cmd;
    }

    private static int Run(bool reembed)
    {
        using var sp = CliServices.Build();
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();

        long total = 0;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM messages WHERE body_html IS NOT NULL";
            total = Convert.ToInt64(countCmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        if (total == 0)
        {
            Console.WriteLine("No messages with body_html. Nothing to do.");
            return 0;
        }

        Console.WriteLine($"Re-deriving body_text for {total:N0} messages...");

        // Fetch all (id, subject, body_html) into memory first. Subject is
        // needed by ReplyTrimmer to distinguish replies from forwards. The
        // DB has 2.7K test rows / a few hundred MB max; for a real archive
        // an ORDER BY id cursor would be safer but this isn't worth the
        // complexity yet.
        var rows = new List<(long Id, string? Subject, string Html)>(checked((int)total));
        using (var fetch = conn.CreateCommand())
        {
            fetch.CommandText = "SELECT id, subject, body_html FROM messages WHERE body_html IS NOT NULL ORDER BY id";
            using var r = fetch.ExecuteReader();
            while (r.Read())
            {
                rows.Add((
                    r.GetInt64(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.GetString(2)));
            }
        }

        long updated = 0, errors = 0;
        using (var tx = conn.BeginTransaction())
        using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE messages SET body_text = $body WHERE id = $id";
            var bodyParam = update.CreateParameter();
            bodyParam.ParameterName = "$body";
            update.Parameters.Add(bodyParam);
            var idParam = update.CreateParameter();
            idParam.ParameterName = "$id";
            update.Parameters.Add(idParam);

            foreach (var (id, subject, html) in rows)
            {
                string newText;
                try
                {
                    newText = HtmlToText.Convert(html);
                    if (!string.IsNullOrEmpty(newText))
                    {
                        newText = ReplyTrimmer.Trim(newText, subject);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  id={id}: convert failed ({ex.GetType().Name}: {ex.Message})");
                    errors++;
                    continue;
                }

                bodyParam.Value = string.IsNullOrEmpty(newText) ? (object)DBNull.Value : newText;
                idParam.Value = id;
                update.ExecuteNonQuery();
                updated++;

                if (updated % 500 == 0) Console.WriteLine($"  ... {updated:N0}/{total:N0}");
            }
            tx.Commit();
        }

        Console.WriteLine($"Updated body_text on {updated:N0} messages ({errors:N0} errors).");

        if (reembed)
        {
            var chunks = sp.GetRequiredService<ChunkRepository>();
            var cleared = chunks.ClearEmbeddings(folderFilter: null);
            Console.WriteLine($"Cleared embeddings on {cleared:N0} messages. Run the embedder to regenerate.");
        }
        else
        {
            Console.WriteLine("FTS5 is updated automatically via triggers. Embeddings still reflect the OLD body_text.");
            Console.WriteLine("Run `mailvec reindex --all` (or rerun with --reembed) when you're ready to refresh vectors.");
        }
        return errors == 0 ? 0 : 1;
    }
}
