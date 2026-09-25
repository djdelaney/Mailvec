using System.Text;

namespace Mailvec.DevCorpus;

/// <summary>
/// Hosted embedding profiles env.sh can configure, for places with no Ollama.
/// Everything here is non-secret: the profiles use <c>Auth:Scheme=none</c>, so
/// Mailvec sends no Authorization header at all, and the key is attached
/// OUTSIDE the process — in a Claude cloud session, by the egress proxy from
/// an environment API credential scoped to the provider's host. Nothing in
/// the session (process, environment, files, transcript) ever holds it.
/// See docs/contributing/dev-corpus.md.
///
/// Sending mail text to a hosted provider is acceptable here only because the
/// corpus is invented. Never point one of these at real mail.
/// </summary>
public static class HostedEmbedding
{
    public const string Fireworks = "fireworks";
    public static readonly IReadOnlyList<string> Names = [Fireworks];

    /// <summary>The env.sh block for <paramref name="name"/>, or "" for none.</summary>
    public static string EnvSh(string? name)
    {
        if (name is null) return "";
        if (name != Fireworks) throw new ArgumentException($"Unknown embedding profile '{name}'.", nameof(name));

        // The shape of the reference profile in docs/proposals/embedding-providers.md
        // ("Proposed configuration"), at 1024 dims, with its own space id: a
        // dev database must never be mistaken for any other space.
        // The profile name must be a valid shell identifier (no '-'): it is
        // spelled inside every exported variable name, and bash refuses
        // `export A-B=…` with an error sourcing does not stop on.
        const string p = "Embedding__Profiles__fireworks_dev__";
        var sb = new StringBuilder();
        sb.Append('\n');
        sb.Append("# ── Hosted embedding: Fireworks qwen3-embedding-8b, 1024 dims ──────────────\n");
        sb.Append("# No key here, by design: Auth:Scheme=none sends no Authorization header, and\n");
        sb.Append("# the Claude cloud environment's API credential for api.fireworks.ai adds it\n");
        sb.Append("# at the egress proxy. Without that credential every call is a 401.\n");
        sb.Append("export Embedding__ActiveProfile='fireworks_dev'\n");
        sb.Append($"export {p}Protocol='openai-compatible'\n");
        sb.Append($"export {p}ProviderId='fireworks'\n");
        sb.Append($"export {p}Endpoint='https://api.fireworks.ai/inference/v1/embeddings'\n");
        sb.Append($"export {p}Request__Model='accounts/fireworks/models/qwen3-embedding-8b'\n");
        sb.Append($"export {p}Request__ModelParameter='required'\n");
        sb.Append($"export {p}Request__DimensionsParameter='send'\n");
        sb.Append($"export {p}Request__EncodingFormat='float'\n");
        sb.Append($"export {p}OutputDimensions='1024'\n");
        sb.Append($"export {p}SpaceId='fireworks:qwen3-embedding-8b:1024:devcorpus'\n");
        // printf, not $'…': the value carries a newline and env.sh stays POSIX-sourceable.
        sb.Append($"export {p}Text__QueryPrefix=\"$(printf 'Instruct: Given a web search query, retrieve relevant passages that answer the query\\nQuery: ')\"\n");
        sb.Append($"export {p}Auth__Scheme='none'\n");
        sb.Append("# Route through HTTPS_PROXY: the cloud session's egress only works that way,\n");
        sb.Append("# and it is where the key is attached. Refused for a profile holding a key.\n");
        sb.Append($"export {p}Proxy='environment'\n");
        sb.Append("# No vision model here either: skip the OCR pass rather than log it failing.\n");
        sb.Append("export Embedder__OcrEnabled='false'\n");
        return sb.ToString();
    }
}
