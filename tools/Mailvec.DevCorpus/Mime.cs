using System.Text;

namespace Mailvec.DevCorpus;

/// <summary>
/// Hand-rolled MIME assembly. Messages are built as a string in which every
/// char is one byte (U+0000–U+00FF, i.e. Latin-1 as a byte carrier) and
/// written with <see cref="Encoding.Latin1"/> — so 8-bit bodies land on disk
/// exactly as authored, including bytes no declared charset explains.
/// Line endings are LF, as mbsync writes them.
/// </summary>
internal static class Mime
{
    public static byte[] ToBytes(string message) => Encoding.Latin1.GetBytes(message);

    /// <summary>UTF-8 bytes of <paramref name="s"/>, carried one byte per char.</summary>
    public static string Utf8(string s) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(s));

    /// <summary>Latin-1 bytes of <paramref name="s"/> (throws on anything outside it).</summary>
    public static string Latin1(string s)
    {
        foreach (var c in s)
        {
            if (c > 'ÿ') throw new ArgumentException($"'{c}' is not Latin-1", nameof(s));
        }
        return s;
    }

    /// <summary>
    /// Windows-1252 bytes of <paramref name="s"/>. Only the handful of
    /// characters the corpus uses above Latin-1 are mapped — enough to put
    /// the 0x80–0x9F range (where 1252 and Latin-1 disagree) on disk.
    /// </summary>
    public static string Cp1252(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '€' => '\u0080',
                '‘' => '\u0091',
                '’' => '\u0092',
                '“' => '\u0093',
                '”' => '\u0094',
                '–' => '\u0096',
                '—' => '\u0097',
                <= 'ÿ' => c,
                _ => throw new ArgumentException($"'{c}' is not mapped to Windows-1252", nameof(s)),
            });
        }
        return sb.ToString();
    }

    public static string Base64(byte[] bytes) =>
        Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\n") + "\n";

    /// <summary>Quoted-printable over bytes already carried one per char.</summary>
    public static string QuotedPrintable(string byteString)
    {
        var sb = new StringBuilder();
        var lineLen = 0;
        foreach (var c in byteString)
        {
            if (c == '\n')
            {
                sb.Append('\n');
                lineLen = 0;
                continue;
            }
            var token = c is >= ' ' and <= '~' and not '=' ? c.ToString() : $"={(int)c:X2}";
            if (lineLen + token.Length > 75)
            {
                sb.Append("=\n");
                lineLen = 0;
            }
            sb.Append(token);
            lineLen += token.Length;
        }
        return sb.ToString();
    }

    /// <summary>RFC 2047 encoded-word (UTF-8, base64).</summary>
    public static string EncodedWord(string s) =>
        $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(s))}?=";

    /// <summary>RFC 2231 extended parameter value: utf-8''percent-encoded.</summary>
    public static string Rfc2231(string s)
    {
        var sb = new StringBuilder("utf-8''");
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                || b == '.' || b == '-' || b == '_')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2"));
            }
        }
        return sb.ToString();
    }

    /// <summary>An RFC 5322 date as mail clients write it.</summary>
    public static string Date(DateTimeOffset d)
    {
        var offset = d.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();
        return d.ToString("ddd, dd MMM yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
               + $" {sign}{abs.Hours:00}{abs.Minutes:00}";
    }

    /// <summary>
    /// A header block. Null values are omitted, so a scenario can drop
    /// Message-ID or Date by passing null.
    /// </summary>
    public static string Headers(params (string Name, string? Value)[] headers)
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in headers)
        {
            if (value is not null) sb.Append(name).Append(": ").Append(value).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>A leaf part: its own headers, a blank line, then the (already encoded) body.</summary>
    public static string Part(string headers, string body) =>
        headers + "\n" + (body.EndsWith('\n') ? body : body + "\n");

    /// <summary>A multipart body with a fixed boundary, so the output is deterministic.</summary>
    public static string Multipart(string boundary, params string[] parts)
    {
        var sb = new StringBuilder("This is a multi-part message in MIME format.\n");
        foreach (var part in parts)
        {
            sb.Append("\n--").Append(boundary).Append('\n').Append(part);
        }
        sb.Append("\n--").Append(boundary).Append("--\n");
        return sb.ToString();
    }

    public static string TextPlain(string text, string charset = "utf-8") =>
        Part($"Content-Type: text/plain; charset={charset}\nContent-Transfer-Encoding: 8bit\n", Utf8(text));

    public static string TextHtml(string html) =>
        Part("Content-Type: text/html; charset=utf-8\nContent-Transfer-Encoding: 8bit\n", Utf8(html));

    /// <summary>A base64 attachment part. <paramref name="dispositionFileName"/> overrides the plain filename parameter (e.g. an RFC 2231 form).</summary>
    public static string Attachment(string contentType, string fileName, byte[] bytes,
        string disposition = "attachment", string? contentId = null, string? dispositionFileName = null)
    {
        var fileParam = dispositionFileName ?? $"filename=\"{fileName}\"";
        var headers = $"Content-Type: {contentType}; name=\"{fileName}\"\n"
                      + $"Content-Disposition: {disposition}; {fileParam}\n"
                      + (contentId is null ? "" : $"Content-ID: <{contentId}>\n")
                      + "Content-Transfer-Encoding: base64\n";
        return Part(headers, Base64(bytes));
    }
}
