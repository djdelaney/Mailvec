using System.Runtime.Versioning;

// The sampler renders PDF pages through Mailvec.Pdf (PDFium, native), which is
// platform-gated to these three. Declaring it once at the assembly level rather
// than threading [SupportedOSPlatform] down through Program's top-level
// statements — the whole tool has the same reach as the renderer it depends on.
[assembly: SupportedOSPlatform("macos")]
[assembly: SupportedOSPlatform("linux")]
[assembly: SupportedOSPlatform("windows")]
