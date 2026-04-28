using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mailvec.Core.Search;

namespace Mailvec.Core.Eval;

/// <summary>
/// A hand-labeled set of queries used to measure search quality. The file
/// shape is documented in <c>eval/README.md</c>; this class is the in-memory
/// form. Identity of expected results is the RFC Message-ID header (stable
/// across reindexes), not the SQLite row id.
/// </summary>
public sealed class EvalQuerySet
{
    public int Version { get; set; } = 1;
    public List<EvalQuery> Queries { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Message-IDs are wrapped in <...>; keep them literal in the file so a
        // human can copy-paste the value back into a search.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static EvalQuerySet Load(string path)
    {
        using var stream = File.OpenRead(path);
        var set = JsonSerializer.Deserialize<EvalQuerySet>(stream, JsonOpts)
            ?? throw new InvalidDataException($"Empty or invalid query set at {path}.");
        set.Validate(path);
        return set;
    }

    public static EvalQuerySet LoadOrEmpty(string path)
    {
        if (!File.Exists(path)) return new EvalQuerySet();
        return Load(path);
    }

    public void Save(string path)
    {
        Validate(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, this, JsonOpts);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Generates the next sequential id ("q001", "q002", ...) not already in use.</summary>
    public string NextSequentialId()
    {
        var max = 0;
        foreach (var q in Queries)
        {
            if (q.Id.Length >= 2 && q.Id[0] == 'q' && int.TryParse(q.Id.AsSpan(1), out var n) && n > max)
                max = n;
        }
        return $"q{max + 1:D3}";
    }

    private void Validate(string path)
    {
        if (Version != 1) throw new InvalidDataException($"{path}: unsupported version {Version}; expected 1.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in Queries)
        {
            if (string.IsNullOrWhiteSpace(q.Id)) throw new InvalidDataException($"{path}: query missing id.");
            if (!ids.Add(q.Id)) throw new InvalidDataException($"{path}: duplicate query id '{q.Id}'.");
            if (string.IsNullOrWhiteSpace(q.Query)) throw new InvalidDataException($"{path}: query '{q.Id}' missing 'query' text.");
            foreach (var r in q.Relevant)
            {
                if (string.IsNullOrWhiteSpace(r.MessageId))
                    throw new InvalidDataException($"{path}: query '{q.Id}' has a relevant entry with empty messageId.");
            }
        }
    }
}

public sealed class EvalQuery
{
    public string Id { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public EvalQueryFilters? Filters { get; set; }
    public List<RelevantEntry> Relevant { get; set; } = [];
    public string? Notes { get; set; }
}

public sealed class EvalQueryFilters
{
    public string? Folder { get; set; }
    public DateTimeOffset? DateFrom { get; set; }
    public DateTimeOffset? DateTo { get; set; }
    public string? FromContains { get; set; }
    public string? FromExact { get; set; }

    public SearchFilters ToSearchFilters() =>
        new(Folder: Folder, DateFrom: DateFrom, DateTo: DateTo, FromContains: FromContains, FromExact: FromExact);
}

/// <summary>
/// One expected-relevant message for a query. Serialized as a bare string
/// when grade=1 (the common case), or as <c>{messageId, grade}</c> for
/// graded relevance. The polymorphism is preserved on round-trip.
/// </summary>
[JsonConverter(typeof(RelevantEntryConverter))]
public sealed record RelevantEntry(string MessageId, double Grade = 1.0);

internal sealed class RelevantEntryConverter : JsonConverter<RelevantEntry>
{
    public override RelevantEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new RelevantEntry(reader.GetString()!);
        }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            string? messageId = null;
            double grade = 1.0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("Expected property name in relevant entry.");
                var prop = reader.GetString();
                reader.Read();
                if (string.Equals(prop, "messageId", StringComparison.OrdinalIgnoreCase))
                    messageId = reader.GetString();
                else if (string.Equals(prop, "grade", StringComparison.OrdinalIgnoreCase))
                    grade = reader.GetDouble();
                else
                    reader.Skip();
            }
            if (messageId is null) throw new JsonException("Relevant entry missing 'messageId'.");
            return new RelevantEntry(messageId, grade);
        }
        throw new JsonException($"Unexpected token {reader.TokenType} for relevant entry; expected string or object.");
    }

    public override void Write(Utf8JsonWriter writer, RelevantEntry value, JsonSerializerOptions options)
    {
        if (value.Grade == 1.0)
        {
            writer.WriteStringValue(value.MessageId);
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("messageId", value.MessageId);
            writer.WriteNumber("grade", value.Grade);
            writer.WriteEndObject();
        }
    }
}
