using static Mailvec.DevCorpus.Mime;

namespace Mailvec.DevCorpus;

/// <summary>The eval query set and the --hazards cases.</summary>
internal static class Extras
{
    /// <summary>
    /// Labelled queries whose answers the corpus controls. A PLUMBING check
    /// for `mailvec eval` over a corpus anyone can build — not a quality
    /// measure. Numbers from it never belong in baselines/, which measure the
    /// frozen real corpus.
    /// </summary>
    public static IReadOnlyList<EvalQuery> Eval() =>
    [
        new("dev01", "cedar boards timber order", ["s01-timber@example.com"]),
        new("dev02", "Lisbon trip dates", ["s03a-lisbon@example.net", "s03b-lisbon@example.org", "s03c-lisbon@example.net"],
            Notes: "One thread of three."),
        new("dev03", "invoice 4417 harbor hardware", ["s15-invoice@harbor-hardware.example"],
            Notes: "The number is in the PDF attachment."),
        new("dev04", "greenhouse sensors roadmap", ["s18-roadmap@example.com"],
            Notes: "DOCX, XLSX and PPTX text."),
        new("dev05", "café crème Montréal", ["s06-cafe@example.fr"], Notes: "Latin-1 quoted-printable body."),
        new("dev06", "async cancellation", ["s14-async@example.org"], Folder: "Lists/rust-users",
            Notes: "Folder filter on a message filed in two folders."),
        new("dev07", "seed swap saturday", ["s05-sprout@sprout.example"], Notes: "Text inside a font-size:0 wrapper."),
        new("dev08", "shed build day", ["s19-invite@example.com"], Notes: "iCalendar attachment."),
        new("dev09", "東京 meetup", ["s08-tokyo@example.jp"]),
        new("dev10", "boiler service", ["s31-boiler@heatco.example"], Folder: "Archive/2025"),
    ];

    public static IReadOnlyList<Hazard> Hazards(Catalog catalog)
    {
        var owner = Catalog.Owner;
        var d = Catalog.At(2025, 11, 1, 12, 0);
        return
        [
            new Hazard("h01-zip-bomb",
                "A DOCX whose one part expands past the extractor's per-part character ceiling from a small package. Must fail fast ('failed'), not allocate gigabytes.",
                [catalog.Mail("INBOX", Catalog.Message("h01-bomb@hazard.example", "Hazard <h@hazard.example>", owner, "Hazard: zip bomb", d,
                    Part("Content-Type: multipart/mixed; boundary=\"h01\"\n", Multipart("h01",
                        TextPlain("A small attachment that is not small once unzipped.\n"),
                        Attachment(Documents.DocxType, "bomb.docx", Documents.DocxBomb())))))],
                new Dictionary<string, byte[]>(), new Dictionary<string, string>()),

            new Hazard("h02-oversize",
                "A 26 MB attachment, over Indexer:AttachmentMaxBytes (25 MB): 'oversize', never decoded for extraction.",
                [catalog.Mail("INBOX", Catalog.Message("h02-oversize@hazard.example", "Hazard <h@hazard.example>", owner, "Hazard: oversize attachment", d,
                    Part("Content-Type: multipart/mixed; boundary=\"h02\"\n", Multipart("h02",
                        TextPlain("An attachment over the extraction ceiling.\n"),
                        Attachment("application/pdf", "huge-scan.pdf",
                            Enumerable.Range(0, 26 * 1024 * 1024).Select(i => (byte)(i * 7 % 251)).ToArray())))))],
                new Dictionary<string, byte[]>(), new Dictionary<string, string>()),

            new Hazard("h03-symlink-escape",
                "A Maildir entry that is a symlink to a file OUTSIDE the Maildir root (outside/private.eml). MaildirAttachmentReader refuses to read through such a link; the scanner (observed 2026-09-25) follows it and indexes the target. That disagreement is an open question, not a settled rule.",
                [],
                new Dictionary<string, byte[]>
                {
                    ["outside/private.eml"] = Catalog.Message("h03-outside@hazard.example", "Hazard <h@hazard.example>", owner,
                        "Hazard: a file outside the Maildir", d, TextPlain("If this is searchable, a symlink carried it in.\n")),
                },
                new Dictionary<string, string>
                {
                    [$"INBOX/cur/{catalog.FileName("S")}"] = "outside/private.eml",
                }),

            new Hazard("h04-folder-named-tmp",
                "An IMAP folder literally named 'tmp' (Projects/tmp): indistinguishable from Maildir internals by name, so the scanner skips it and logs a warning.",
                [catalog.Mail("Projects/tmp", Catalog.Message("h04-tmp@hazard.example", "Hazard <h@hazard.example>", owner,
                    "Hazard: folder named tmp", d, TextPlain("This folder's name collides with the Maildir internals.\n")))],
                new Dictionary<string, byte[]>(), new Dictionary<string, string>()),
        ];
    }
}
