using MimeKit;

namespace Mailvec.Parsing;

/// <summary>
/// The one set of <see cref="ParserOptions"/> every MIME parse in Mailvec
/// uses. Every <c>MimeMessage.Load</c> must pass <see cref="Options"/>.
/// One that falls back to <see cref="ParserOptions.Default"/> parses the
/// common case identically and differs only on the mail this class exists for,
/// so nothing fails until such a message arrives.
/// </summary>
public static class MimeParsing
{
    /// <summary>
    /// The longest single header field the parser accepts, in raw bytes.
    /// MimeKit 5 introduced <see cref="ParserOptions.MaxHeaderLength"/> with a
    /// 16 KB default and throws <see cref="FormatException"/> past it.
    /// MimeKit 4 had no limit, and real mail carries header fields well over
    /// 16 KB (long recipient lists, large signed or key-carrying headers). At
    /// the default, those messages fail to parse at all: an already-indexed
    /// one on its next re-parse, a new one before it is ever indexed. The
    /// message as a whole is already bounded (<c>Indexer:MaxMessageBytes</c>,
    /// the parse service's request cap), so 1 MB keeps a ceiling on any one
    /// field without refusing mail that MimeKit 4 parsed.
    /// </summary>
    public const int MaxHeaderLength = 1024 * 1024;

    private static readonly ParserOptions Shared = Build();

    /// <summary>
    /// A copy of the shared options, so a caller that tweaks its copy can't
    /// change every other parse in the process.
    /// </summary>
    public static ParserOptions Options => Shared.Clone();

    private static ParserOptions Build()
    {
        var options = ParserOptions.Default.Clone();
        options.MaxHeaderLength = MaxHeaderLength;
        return options;
    }
}
