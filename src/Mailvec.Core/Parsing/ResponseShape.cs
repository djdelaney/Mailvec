using System.Text.Json;
using Mailvec.Parsing.Contracts;

namespace Mailvec.Core.Parsing;

/// <summary>
/// A pre-scan of a parse response's JSON, run over the buffered bytes
/// <em>before</em> <see cref="JsonSerializer"/> materialises anything.
///
/// <para><b>Why the byte ceiling is not enough.</b> <c>Parser:MaxResponseBytes</c>
/// bounds the serialized response; it does not bound what deserializing it
/// allocates. Compact JSON expands: the review measured
/// <c>{"attachments":[{},{},…]}</c> with 100,000 entries at 300 KB on the wire
/// and ~15 MB of object graph — 50x — so a response comfortably under the
/// 64 MB ceiling could still allocate gigabytes inside the archive-holding
/// caller. A count check after deserialization is too late; this one runs
/// first, with <see cref="Utf8JsonReader"/> over the buffer, and allocates
/// nothing beyond the reader's stack state.</para>
///
/// <para>Two limits, both far above any honest answer: no array longer than
/// <see cref="MaxArrayElements"/> (a message with 10,000 attachments or
/// recipients is not a message), and no more than <see cref="MaxTokens"/>
/// tokens in the whole document (the amplification comes from many small
/// objects, and every object costs tokens). Base64 payloads are single string
/// tokens and are bounded by the byte ceiling. A violation is
/// <see cref="ParseFailureKind.Crashed"/>: the service, not the document.</para>
/// </summary>
public static class ResponseShape
{
    public const int MaxArrayElements = 10_000;
    public const int MaxTokens = 1_000_000;

    public static void Check(ReadOnlySpan<byte> json)
    {
        const int maxDepth = 64;
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = maxDepth });
        // Our own container stack — whether each open container is an array,
        // and the element count of each open array — so element counting does
        // not depend on the reader's depth accounting at start tokens. Depth is
        // capped by the reader, so fixed stacks suffice.
        Span<bool> isArray = stackalloc bool[maxDepth + 1];
        Span<int> counts = stackalloc int[maxDepth + 1];
        var depth = 0;
        var tokens = 0;
        try
        {
            while (reader.Read())
            {
                if (++tokens > MaxTokens)
                    throw new ParseException(ParseFailureKind.Crashed,
                        $"The parse service's response has more than {MaxTokens:N0} JSON tokens.");

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartArray:
                    case JsonTokenType.StartObject:
                        // A container directly inside an array is one element.
                        if (depth > 0 && isArray[depth - 1] && ++counts[depth - 1] > MaxArrayElements) throw TooLong();
                        isArray[depth] = reader.TokenType == JsonTokenType.StartArray;
                        counts[depth] = 0;
                        depth++;
                        break;
                    case JsonTokenType.EndArray:
                    case JsonTokenType.EndObject:
                        depth--;
                        break;
                    case JsonTokenType.String:
                    case JsonTokenType.Number:
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                    case JsonTokenType.Null:
                        // A primitive directly inside an array is one element;
                        // inside an object it is a property value.
                        if (depth > 0 && isArray[depth - 1] && ++counts[depth - 1] > MaxArrayElements) throw TooLong();
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            // Malformed JSON is the deserializer's finding to make; report it
            // the same way it would.
            throw new ParseException(ParseFailureKind.Crashed,
                "The parse service answered with a body that is not a parse result.", ex);
        }

        static ParseException TooLong() => new(ParseFailureKind.Crashed,
            $"The parse service's response has an array longer than {MaxArrayElements:N0} elements.");
    }
}
