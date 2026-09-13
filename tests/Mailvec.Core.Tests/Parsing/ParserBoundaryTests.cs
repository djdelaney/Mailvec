using System.Reflection;
using Mailvec.Core.Data;
using Mailvec.Parsing;
using Mailvec.Parsing.Contracts;

namespace Mailvec.Core.Tests.Parsing;

/// <summary>
/// The project boundary IS the isolation (docs/proposals/attachment-parser-isolation.md):
/// the processes that hold the mailbox reference Core, Core references only
/// the Contracts, and every parser lives in Mailvec.Parsing, which never
/// references Core. A parser package creeping back into Core — or Core
/// creeping into Parsing — would silently put MimeKit / PdfPig / PDFium back
/// into the indexer, embedder and MCP binaries (or SQLite and the config
/// loader into the future parse container) with nothing failing. These assert
/// the assembly graph, which is the artefact that can't drift quietly.
/// </summary>
public class ParserBoundaryTests
{
    private static readonly string[] ParserAssemblies =
    [
        "MimeKit", "BouncyCastle.Cryptography",
        "UglyToad.PdfPig", "UglyToad.PdfPig.Core", "UglyToad.PdfPig.Tokenization", "UglyToad.PdfPig.Fonts",
        "DocumentFormat.OpenXml", "DocumentFormat.OpenXml.Framework", "System.IO.Packaging",
        "AngleSharp",
        "PDFtoImage", "SkiaSharp", "BitMiracle.LibTiff.NET",
    ];

    private static string[] ReferencesOf(Assembly asm) =>
        asm.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Core_references_no_parser_assembly_and_not_the_parsing_project()
    {
        var refs = ReferencesOf(typeof(ConnectionFactory).Assembly);

        refs.ShouldNotContain("Mailvec.Parsing");
        foreach (var parser in ParserAssemblies)
            refs.ShouldNotContain(parser, $"Mailvec.Core must not reference {parser}");
        refs.ShouldContain("Mailvec.Parsing.Contracts");
    }

    [Fact]
    public void The_contracts_reference_nothing_of_ours_and_no_parser()
    {
        var refs = ReferencesOf(typeof(IMailParser).Assembly);

        refs.ShouldNotContain("Mailvec.Core");
        refs.ShouldNotContain("Mailvec.Parsing");
        foreach (var parser in ParserAssemblies)
            refs.ShouldNotContain(parser);
    }

    [Fact]
    public void The_parsing_project_never_references_Core()
    {
        // The future parse container references Mailvec.Parsing and nothing
        // else of ours; a Core reference here would drag SQLite, the shared
        // config loader and the credential-bearing options into it.
        var refs = ReferencesOf(typeof(InProcessParser).Assembly);

        refs.ShouldNotContain("Mailvec.Core");
        refs.ShouldContain("Mailvec.Parsing.Contracts");
        refs.ShouldContain("MimeKit");
    }
}
