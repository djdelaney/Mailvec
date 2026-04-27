namespace Mailvec.Core.Options;

public sealed class IngestOptions
{
    public const string SectionName = "Ingest";

    public string MaildirRoot { get; set; } = "~/Mail/Fastmail";
}
