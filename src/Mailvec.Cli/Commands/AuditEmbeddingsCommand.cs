using System.CommandLine;
using Mailvec.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Walks chunk_embeddings, deserializes each vector, and flags rows whose
/// vectors look like Ollama returned junk: all zeros, NaN/Inf, or an L2 norm
/// far from 1.0 (mxbai-embed-large outputs are L2-normalized).
/// </summary>
internal static class AuditEmbeddingsCommand
{
    public static Command Build()
    {
        var sampleOpt = new Option<int>("--sample") { DefaultValueFactory = _ => 10, Description = "How many suspicious rows to show per category." };
        var normLowOpt = new Option<double>("--norm-low") { DefaultValueFactory = _ => 0.5, Description = "Flag vectors whose L2 norm is below this." };
        var normHighOpt = new Option<double>("--norm-high") { DefaultValueFactory = _ => 1.5, Description = "Flag vectors whose L2 norm is above this." };

        var cmd = new Command("audit-embeddings", "Scan chunk_embeddings for zero/NaN/garbage vectors.")
        {
            sampleOpt,
            normLowOpt,
            normHighOpt,
        };

        cmd.SetAction(parseResult => Run(
            parseResult.GetValue(sampleOpt),
            parseResult.GetValue(normLowOpt),
            parseResult.GetValue(normHighOpt)));
        return cmd;
    }

    private static int Run(int sample, double normLow, double normHigh)
    {
        using var sp = CliServices.Build();
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();

        long total = 0, zero = 0, nan = 0, lowNorm = 0, highNorm = 0;
        var zeroSamples = new List<RowInfo>();
        var nanSamples = new List<RowInfo>();
        var lowSamples = new List<RowInfo>();
        var highSamples = new List<RowInfo>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.chunk_id, e.embedding, c.message_id, c.chunk_index, m.subject
            FROM chunk_embeddings e
            JOIN chunks   c ON c.id = e.chunk_id
            JOIN messages m ON m.id = c.message_id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            total++;
            var chunkId = reader.GetInt64(0);
            var blob = (byte[])reader["embedding"];
            var messageId = reader.GetInt64(2);
            var chunkIndex = reader.GetInt32(3);
            var subject = reader.IsDBNull(4) ? null : reader.GetString(4);

            var vec = VectorBlob.Deserialize(blob);

            bool allZero = true;
            bool hasBadFloat = false;
            double sumSq = 0.0;
            foreach (var v in vec)
            {
                if (float.IsNaN(v) || float.IsInfinity(v)) { hasBadFloat = true; break; }
                if (v != 0f) allZero = false;
                sumSq += (double)v * v;
            }

            var info = new RowInfo(chunkId, messageId, chunkIndex, subject, hasBadFloat ? double.NaN : Math.Sqrt(sumSq));

            if (hasBadFloat)
            {
                nan++;
                if (nanSamples.Count < sample) nanSamples.Add(info);
                continue;
            }
            if (allZero)
            {
                zero++;
                if (zeroSamples.Count < sample) zeroSamples.Add(info);
                continue;
            }
            if (info.Norm < normLow)
            {
                lowNorm++;
                if (lowSamples.Count < sample) lowSamples.Add(info);
            }
            else if (info.Norm > normHigh)
            {
                highNorm++;
                if (highSamples.Count < sample) highSamples.Add(info);
            }
        }

        Console.WriteLine($"Scanned {total:N0} embedding row(s).");
        Console.WriteLine();
        Console.WriteLine($"  All-zero vectors:    {zero:N0}");
        Console.WriteLine($"  NaN/Inf vectors:     {nan:N0}");
        Console.WriteLine($"  Norm < {normLow:F2}:        {lowNorm:N0}");
        Console.WriteLine($"  Norm > {normHigh:F2}:        {highNorm:N0}");
        Console.WriteLine();

        PrintSamples("All-zero", zeroSamples);
        PrintSamples("NaN/Inf", nanSamples);
        PrintSamples($"Norm < {normLow:F2}", lowSamples);
        PrintSamples($"Norm > {normHigh:F2}", highSamples);

        if (zero == 0 && nan == 0 && lowNorm == 0 && highNorm == 0)
        {
            Console.WriteLine("No suspicious vectors found.");
        }
        return 0;
    }

    private static void PrintSamples(string label, List<RowInfo> samples)
    {
        if (samples.Count == 0) return;
        Console.WriteLine($"--- {label} samples ---");
        foreach (var s in samples)
        {
            var norm = double.IsNaN(s.Norm) ? "  NaN " : s.Norm.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
            Console.WriteLine($"  chunk_id={s.ChunkId}  msg_id={s.MessageId}  idx={s.ChunkIndex}  norm={norm}  subject={s.Subject ?? "(none)"}");
        }
        Console.WriteLine();
    }

    private sealed record RowInfo(long ChunkId, long MessageId, int ChunkIndex, string? Subject, double Norm);
}
