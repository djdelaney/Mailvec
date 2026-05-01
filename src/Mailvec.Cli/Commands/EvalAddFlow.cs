using System.Globalization;
using Mailvec.Core.Data;
using Mailvec.Core.Eval;
using Mailvec.Core.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Shared flow for `eval-add` and `eval-import`: given a query + filters, either
/// (a) run a search and let the user mark relevant results, or (b) take an
/// explicit list of pinned Message-IDs as the relevant set. Validates pinned
/// IDs against the messages table so a typo doesn't get silently saved as
/// "the right answer was always missing".
/// </summary>
internal static class EvalAddFlow
{
    public sealed record Args(
        string Query,
        EvalQueryFilters? Filters,
        EvalMode Mode,
        int TopK,
        string? IdOverride,
        string? Notes,
        IReadOnlyList<string>? PinRelevantIds,   // null/empty = interactive
        bool Yes,
        string QuerySetPath);

    public static async Task<int> RunAsync(IServiceProvider sp, Args args, CancellationToken ct)
    {
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();

        var pinned = args.PinRelevantIds is null
            ? Array.Empty<string>()
            : args.PinRelevantIds.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

        IReadOnlyList<RelevantEntry> relevant;
        IReadOnlyList<EvalAddCandidate> displayCandidates;

        if (pinned.Length > 0)
        {
            var (entries, lookups) = ValidatePinned(sp, pinned);
            if (entries is null) return 2;
            relevant = entries;
            displayCandidates = lookups;
        }
        else
        {
            var candidates = await EvalAddCandidates.GatherAsync(sp, args.Query, args.Mode, args.TopK, args.Filters?.ToSearchFilters(), ct);
            if (candidates.Count == 0)
            {
                Console.WriteLine("(no matches — nothing to label)");
                return 1;
            }

            EvalAddCandidates.PrintCandidates(candidates);

            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("\nstdin is not a TTY; the labeling prompt requires interactive input.");
                return 2;
            }

            var picks = EvalAddCandidates.PromptForPicks(candidates.Count);
            if (picks.Count == 0)
            {
                Console.WriteLine("(no picks — nothing saved)");
                return 0;
            }

            relevant = picks
                .Select(p => new RelevantEntry(candidates[p.Rank - 1].MessageIdHeader, p.Grade))
                .ToList();
            displayCandidates = candidates;
        }

        var set = EvalQuerySet.LoadOrEmpty(args.QuerySetPath);
        var id = args.IdOverride ?? set.NextSequentialId();
        if (set.Queries.Any(q => q.Id == id))
        {
            Console.Error.WriteLine($"Id '{id}' already exists in {args.QuerySetPath}.");
            return 2;
        }

        var newQuery = new EvalQuery
        {
            Id = id,
            Query = args.Query,
            Filters = args.Filters,
            Relevant = relevant.ToList(),
            Notes = args.Notes,
        };

        EvalAddCandidates.PrintSummary(newQuery, displayCandidates);

        if (!args.Yes)
        {
            Console.Write("Save? [y/N]: ");
            var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (confirm != "y" && confirm != "yes")
            {
                Console.WriteLine("(not saved)");
                return 0;
            }
        }

        set.Queries.Add(newQuery);
        set.Save(args.QuerySetPath);
        Console.WriteLine($"Saved {id} → {args.QuerySetPath}  (set now has {set.Queries.Count} quer{(set.Queries.Count == 1 ? "y" : "ies")})");
        return 0;
    }

    /// <summary>
    /// Looks each pinned ID up in `messages` to confirm it actually exists.
    /// Typo'd IDs are caught here rather than after the fact when the eval
    /// silently scores 0 because the "expected" message was never in the corpus.
    /// </summary>
    private static (IReadOnlyList<RelevantEntry>? Entries, IReadOnlyList<EvalAddCandidate> Display) ValidatePinned(IServiceProvider sp, IReadOnlyList<string> pinned)
    {
        var repo = sp.GetRequiredService<MessageRepository>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<RelevantEntry>(pinned.Count);
        var display = new List<EvalAddCandidate>(pinned.Count);

        foreach (var token in pinned)
        {
            // Allow N=G grade syntax for symmetry with the interactive picker.
            string idPart;
            double grade = 1.0;
            var eq = token.IndexOf('=');
            if (eq >= 0)
            {
                idPart = token[..eq];
                if (!double.TryParse(token.AsSpan(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out grade))
                {
                    Console.Error.WriteLine($"Could not parse grade in '{token}'. Use the form <id>=<grade>.");
                    return (null, []);
                }
            }
            else
            {
                idPart = token;
            }

            // Strip surrounding angle brackets if pasted from a header.
            idPart = idPart.Trim().TrimStart('<').TrimEnd('>');

            if (!seen.Add(idPart))
            {
                Console.Error.WriteLine($"Skipping duplicate id '{idPart}'.");
                continue;
            }

            var msg = repo.GetByMessageId(idPart);
            if (msg is null)
            {
                Console.Error.WriteLine($"Message-ID '{idPart}' not found in archive. Refusing to save (use `mailvec search --with-id` to look up the right id).");
                return (null, []);
            }

            entries.Add(new RelevantEntry(idPart, grade));
            display.Add(new EvalAddCandidate(
                MessageIdHeader: idPart,
                Subject: msg.Subject,
                From: msg.FromName ?? msg.FromAddress,
                Date: msg.DateSent,
                Folder: msg.Folder,
                Snippet: string.Empty));
        }

        if (entries.Count == 0)
        {
            Console.Error.WriteLine("--pin-relevant requires at least one valid Message-ID.");
            return (null, []);
        }
        return (entries, display);
    }
}

/// <summary>
/// View-model for the candidate list shown to the user during eval-add.
/// Lifted out of EvalAddCommand so EvalAddFlow can also reuse it for the
/// pinned-IDs path (where there's no real "snippet" — we synthesize a row
/// per validated message).
/// </summary>
internal sealed record EvalAddCandidate(
    string MessageIdHeader,
    string? Subject,
    string? From,
    DateTimeOffset? Date,
    string Folder,
    string Snippet);
