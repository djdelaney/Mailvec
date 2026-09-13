namespace Mailvec.Core.Options;

/// <summary>
/// Where mail content gets parsed. <c>inprocess</c> (the default, and the
/// macOS launchd install) runs the parsers inside the calling process;
/// <c>remote</c> (the container deployment, from phase 2 of
/// docs/proposals/attachment-parser-isolation.md) sends bytes to the
/// <c>parse</c> service at <see cref="Endpoint"/>. Resolved centrally by
/// <c>ParserRegistration.AddMailvecParser</c>; an unknown mode is fatal.
/// </summary>
public sealed class ParserOptions
{
    public const string SectionName = "Parser";

    /// <summary>"inprocess" or "remote".</summary>
    public string Mode { get; set; } = "inprocess";

    /// <summary>Base URL of the parse service; required when <see cref="Mode"/> is remote.</summary>
    public string? Endpoint { get; set; }
}
