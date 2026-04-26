namespace Mailvec.Core.Options;

public sealed class ArchiveOptions
{
    public const string SectionName = "Archive";

    public string MaildirRoot { get; set; } = "~/Mail/Fastmail";
    public string DatabasePath { get; set; } = "~/Library/Application Support/Mailvec/archive.sqlite";
    public string SqliteVecExtensionPath { get; set; } = "./runtimes/osx-arm64/native/vec0.dylib";
}
