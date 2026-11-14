using System.Text;
using Mailvec.Core.Parsing;
using MimeKit;

namespace Mailvec.Core.Tests.Parsing;

public class MessageBodyHasherTests
{
    [Fact]
    public void Same_body_produces_same_hash()
    {
        var a = Build(body: "Hello world");
        var b = Build(body: "Hello world");
        MessageBodyHasher.Hash(a).ShouldBe(MessageBodyHasher.Hash(b));
    }

    [Fact]
    public void Different_body_produces_different_hash()
    {
        var a = Build(body: "Hello world");
        var b = Build(body: "Hello there");
        MessageBodyHasher.Hash(a).ShouldNotBe(MessageBodyHasher.Hash(b));
    }

    [Fact]
    public void Header_only_changes_do_not_affect_hash()
    {
        // The whole point of hashing the body section: post-delivery header
        // rewrites (DKIM-Verified, X-Spam-Score, etc.) shouldn't churn embeddings.
        var a = Build(body: "Hello world", extraHeaders: []);
        var b = Build(body: "Hello world", extraHeaders: [
            ("X-Spam-Score", "0.0"),
            ("DKIM-Verified", "pass"),
        ]);
        MessageBodyHasher.Hash(a).ShouldBe(MessageBodyHasher.Hash(b));
    }

    [Fact]
    public void Subject_change_does_not_affect_hash()
    {
        // Subject lives in headers; body hash should be insensitive.
        var a = Build(body: "Hello world", subject: "Alpha");
        var b = Build(body: "Hello world", subject: "Beta");
        MessageBodyHasher.Hash(a).ShouldBe(MessageBodyHasher.Hash(b));
    }

    [Fact]
    public void Hash_is_lowercase_hex_64_chars()
    {
        var hash = MessageBodyHasher.Hash(Build("anything"));
        hash.Length.ShouldBe(64);
        hash.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Empty_body_produces_stable_hash()
    {
        // Edge case: messages whose body is null shouldn't blow up; they
        // get the SHA-256 of the empty input.
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("a", "a@x"));
        mime.To.Add(new MailboxAddress("b", "b@x"));
        mime.Subject = "no body";
        // Don't set Body — leave it null.
        var h1 = MessageBodyHasher.Hash(mime);
        var h2 = MessageBodyHasher.Hash(mime);
        h1.ShouldBe(h2);
        h1.Length.ShouldBe(64);
    }

    // ---- Serialization tripwire -------------------------------------------
    //
    // Every other test here is RELATIVE (hash(a) == hash(b)), which proves the
    // hasher is consistent with itself but says nothing about whether the
    // bytes MimeKit produces today match the ones already stored in the
    // archive. A MimeKit upgrade that perturbs Body.WriteTo — header folding,
    // a transfer-encoding default, boundary handling — changes the
    // content_hash of every stored message of the affected shape at once.
    // Nothing else would notice: the indexer reads it as a genuine body change
    // on each such message it re-parses, resets its attachments (OCR verdicts
    // included) and re-embeds it, which surfaces only as an embedder and OCR
    // backlog that nobody ordered.
    //
    // So this pins literal bytes, in the three shapes that have moved or that
    // matter most. If one fails after a dependency bump, the hasher is not
    // broken: the library's serialization moved. The decision is whether to
    // take the bump and let every message of that shape re-index as it is next
    // re-parsed, or hold the version. Measure how much of a real mailbox has
    // the shape before deciding; ops/UPGRADING.md has the method. Update a
    // constant ONLY together with that decision.
    //
    // MimeKit 5.0 (taken 2026-11-14) moved two of the three. Its default
    // parser keeps the raw boundary lines and the writer reproduces them, so
    // (a) a CRLF-stored multipart no longer gets LF boundary lines mixed in, and
    // (b) a multipart whose file ends at the closing delimiter with no newline
    // keeps that, where 4.x appended one. The LF shape with a final newline,
    // which is what mbsync writes, hashes identically under 4.18 and 5.0.
    // Measured against a real mailbox, a minority of messages had shape (b)
    // and none had (a); the bump was taken and those re-index on re-parse.

    /// <summary>
    /// A fixed raw message covering the shapes whose serialization is most
    /// likely to drift between MimeKit versions: a multipart boundary, a
    /// quoted-printable non-ASCII text part, an HTML part, a folded part
    /// header, and a base64 attachment. Stored with CRLF line endings.
    /// </summary>
    private const string PinnedRaw =
        "From: Alice <alice@example.com>\r\n" +
        "To: Bob <bob@example.com>\r\n" +
        "Subject: pinned\r\n" +
        "Message-Id: <pinned@example.com>\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: multipart/mixed; boundary=\"MVBOUND\"\r\n" +
        "\r\n" +
        "--MVBOUND\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "Content-Transfer-Encoding: quoted-printable\r\n" +
        "\r\n" +
        "Caf=C3=A9 na=C3=AFve r=C3=A9sum=C3=A9, plus a line long enough to reach t=\r\n" +
        "he soft-break column where folding rules could plausibly differ.\r\n" +
        "--MVBOUND\r\n" +
        "Content-Type: text/html; charset=utf-8\r\n" +
        "Content-Description: a part header long enough that refolding it would ch" +
        "ange the serialized bytes\r\n" +
        "\r\n" +
        "<p>html part</p>\r\n" +
        "--MVBOUND\r\n" +
        "Content-Type: application/pdf; name=\"d.pdf\"\r\n" +
        "Content-Disposition: attachment; filename=\"d.pdf\"\r\n" +
        "Content-Transfer-Encoding: base64\r\n" +
        "\r\n" +
        "JVBERi0xLjQKJcOkw7zDtsOfCjIgMCBvYmoKPDwvTGVuZ3RoIDMgMCBSPj4Kc3RyZWFtCngB\r\n" +
        "--MVBOUND--\r\n";

    // MimeKit 5.0.0. Under 4.x this was 18b826cc…776e, the LF hash below.
    private const string PinnedCrlfHash = "388f05d5e7b51734256c25454759a59fbc8a280ada9e04ece1d942450f530bef";

    // MimeKit 4.18 and 5.0.0 agree on this one. It is the shape mbsync writes.
    private const string PinnedLfHash = "18b826cc3860828d824ddbac10cf91b626a8ed5536788630e8256f32c73c776e";

    // MimeKit 5.0.0. Under 4.x this was also the LF hash above.
    private const string PinnedLfNoFinalNewlineHash = "46525f7a2ac4a5c2f3fad65dfaeddffa97a06ae6cddadf487800e0b6e0a175dd";

    private const string TripwireReason =
        "MimeKit's body serialization changed for this shape. Every stored content_hash of a message "
        + "with this shape is now stale, so each such message re-parsed will read as a body change and "
        + "re-index (attachments, OCR and embeddings). Take that deliberately or hold the version: "
        + "don't just update the constant.";

    private static string HashOf(string raw) =>
        MessageBodyHasher.Hash(MimeMessage.Load(Mailvec.Parsing.MimeParsing.Options, new MemoryStream(Encoding.ASCII.GetBytes(raw))));

    [Fact]
    public void Serialized_body_bytes_are_pinned_across_library_upgrades()
    {
        HashOf(PinnedRaw).ShouldBe(PinnedCrlfHash, TripwireReason);
    }

    [Fact]
    public void Serialized_body_bytes_are_pinned_for_the_LF_shape_mbsync_writes()
    {
        HashOf(PinnedRaw.Replace("\r\n", "\n")).ShouldBe(PinnedLfHash, TripwireReason);
    }

    [Fact]
    public void Serialized_body_bytes_are_pinned_for_a_multipart_with_no_newline_after_the_closing_boundary()
    {
        HashOf(PinnedRaw.Replace("\r\n", "\n").TrimEnd('\n')).ShouldBe(PinnedLfNoFinalNewlineHash, TripwireReason);
    }

    private static MimeMessage Build(string body, string? subject = "Test", IReadOnlyList<(string Name, string Value)>? extraHeaders = null)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        msg.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        if (subject is not null) msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };
        if (extraHeaders is not null)
        {
            foreach (var (name, value) in extraHeaders)
            {
                msg.Headers.Add(name, value);
            }
        }
        return msg;
    }
}
