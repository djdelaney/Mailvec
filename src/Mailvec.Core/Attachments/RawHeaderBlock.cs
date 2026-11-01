using System.Text;

namespace Mailvec.Core.Attachments;

/// <summary>
/// A message's header block as it sits in the <c>.eml</c> — verbatim: original
/// order, folding and line endings, nothing decoded. Produced by
/// <see cref="MaildirAttachmentReader.ReadHeaderBlock"/>.
/// </summary>
/// <param name="Text">The header lines, each with its own line ending; the
/// terminating blank line is not included.</param>
/// <param name="Truncated">True when the block didn't end within the read
/// ceiling, so <paramref name="Text"/> stops at the last whole line read.</param>
public sealed record RawHeaderBlock(string Text, bool Truncated)
{
    /// <summary>
    /// Cut <paramref name="bytes"/> (the start of a message) at its first empty
    /// line. <paramref name="reachedEnd"/> says whether they are the whole file:
    /// a message with no blank line is all headers, but a read that stopped at
    /// the ceiling without finding one is a truncated block.
    /// </summary>
    public static RawHeaderBlock FromBytes(ReadOnlySpan<byte> bytes, bool reachedEnd)
    {
        var pos = 0;
        while (pos < bytes.Length)
        {
            // An empty line — LF or CRLF — ends the block.
            if (bytes[pos] == (byte)'\n' || (bytes[pos] == (byte)'\r' && pos + 1 < bytes.Length && bytes[pos + 1] == (byte)'\n'))
                return new RawHeaderBlock(Decode(bytes[..pos]), Truncated: false);

            var newline = bytes[pos..].IndexOf((byte)'\n');
            if (newline < 0) break; // a line the read didn't finish
            pos += newline + 1;
        }

        // No blank line. Either the file has no body (all of it is headers) or
        // the ceiling cut the block: then drop the partial line, never hand
        // back half a header as if it were whole.
        return reachedEnd
            ? new RawHeaderBlock(Decode(bytes), Truncated: false)
            : new RawHeaderBlock(Decode(bytes[..pos]), Truncated: true);
    }

    /// <summary>
    /// The block split into fields, in order. A field is a line that doesn't
    /// start with whitespace plus its continuation lines; <see cref="RawHeaderField.Raw"/>
    /// keeps the line endings, so concatenating any subsequence of fields is
    /// still verbatim header text. A line with no colon becomes a field with
    /// an empty name, which no filter matches.
    /// </summary>
    public IReadOnlyList<RawHeaderField> Fields()
    {
        var fields = new List<RawHeaderField>();
        string? name = null;
        var raw = new StringBuilder();

        var pos = 0;
        while (pos < Text.Length)
        {
            var newline = Text.IndexOf('\n', pos);
            var end = newline < 0 ? Text.Length : newline + 1;
            var line = Text.AsSpan(pos, end - pos);

            var continuation = line[0] is ' ' or '\t';
            if (!continuation || name is null)
            {
                if (name is not null) fields.Add(new RawHeaderField(name, raw.ToString()));
                raw.Clear();
                var colon = line.IndexOf(':');
                name = !continuation && colon > 0 ? line[..colon].TrimEnd().ToString() : string.Empty;
            }
            raw.Append(line);
            pos = end;
        }
        if (name is not null) fields.Add(new RawHeaderField(name, raw.ToString()));
        return fields;
    }

    /// <summary>
    /// Whether <paramref name="fieldName"/> is selected by any of
    /// <paramref name="patterns"/>: a case-insensitive name, or a prefix ending
    /// in <c>*</c> (<c>ARC-*</c>). A trailing colon is ignored.
    /// </summary>
    public static bool Matches(string fieldName, IEnumerable<string> patterns)
    {
        if (fieldName.Length == 0) return false;
        foreach (var p in patterns)
        {
            var pattern = p.Trim().TrimEnd(':').TrimEnd();
            if (pattern.Length == 0) continue;
            if (pattern.EndsWith('*')
                ? fieldName.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                : fieldName.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // RFC 6532 allows UTF-8 in header values; anything else invalid becomes
    // U+FFFD rather than failing the read.
    private static string Decode(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}

/// <summary>One header field: its name and its raw text, continuation lines and line endings included.</summary>
public sealed record RawHeaderField(string Name, string Raw);
