using System.Globalization;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;

namespace Mailvec.Core.Eval;

/// <summary>
/// Captures <see cref="EvalReportProvenance"/> for a report: the corpus counts
/// from the database and the embedding identity from the resolved profile plus
/// the stored metadata. Read-only — a few COUNT queries and metadata reads.
/// </summary>
public static class EvalProvenance
{
    public static EvalReportProvenance Capture(
        ConnectionFactory connections,
        MetadataRepository metadata,
        MessageRepository messages,
        ResolvedEmbeddingProfile profile,
        string databasePath,
        string? mailvecVersion)
    {
        var (spaceId, configHash) = EmbeddingSpace.ForProfile(profile);
        var storedSpace = metadata.Get(EmbeddingSpace.SpaceIdKey);
        var storedHash = metadata.Get(EmbeddingSpace.ConfigHashKey);

        return new EvalReportProvenance
        {
            MailvecVersion = mailvecVersion,
            DatabasePath = PathExpansion.Collapse(databasePath),
            MessageCount = messages.CountAll(),
            ChunkCount = CountChunks(connections),
            UnembeddedCount = messages.CountUnembedded(),
            Embedding = new EvalReportEmbedding
            {
                Profile = profile.Name,
                Protocol = profile.Protocol,
                ProviderId = profile.ProviderId,
                Model = profile.WireModel,
                Dimensions = profile.OutputDimensions,
                TruncatedFromDimensions = profile.NativeDimensions,
                SpaceId = spaceId,
                ConfigHash = configHash,
                ModelDigest = NullIfEmpty(metadata.Get(EmbeddingSpace.ModelDigestKey)),
                QueryPrefix = NullIfEmpty(profile.QueryPrefix),
                QuerySuffix = NullIfEmpty(profile.QuerySuffix),
                DocumentPrefix = NullIfEmpty(profile.DocumentPrefix),
                DocumentSuffix = NullIfEmpty(profile.DocumentSuffix),
                EndpointIsLoopback = Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback,
                // Absent metadata is unknown, never a mismatch (the
                // EmbeddingSpaceGuard rule), so only a present, different
                // value is recorded.
                StoredSpaceId = Differs(storedSpace, spaceId),
                StoredConfigHash = Differs(storedHash, configHash),
            },
        };
    }

    private static int CountChunks(ConnectionFactory connections)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string? Differs(string? stored, string expected) =>
        stored is { Length: > 0 } && !string.Equals(stored, expected, StringComparison.Ordinal) ? stored : null;
}
