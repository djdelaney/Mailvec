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
    // a transfer-encoding default, boundary handling — changes every
    // content_hash in the corpus at once. Nothing else would notice: the
    // indexer reads it as a genuine body change on each message it re-parses,
    // clears embedded_at, and re-embeds the whole archive, which surfaces only
    // as an embedder backlog that nobody ordered.
    //
    // So this pins literal bytes. If it fails after a dependency bump, the
    // hasher is not broken — the library's serialization moved, and the
    // decision to make is whether to take the bump and eat a full re-embed
    // (see "mailvec switch-model" for the cost shape) or hold the version.
    // Update the constant ONLY together with that decision.

    /// <summary>
    /// A fixed raw message covering the shapes whose serialization is most
    /// likely to drift between MimeKit versions: a multipart boundary, a
    /// quoted-printable non-ASCII text part, an HTML part, a folded part
    /// header, and a base64 attachment.
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

    // MimeKit 4.18.0. See the comment above before changing this value.
    private const string PinnedHash = "18b826cc3860828d824ddbac10cf91b626a8ed5536788630e8256f32c73c776e";

    [Fact]
    public void Serialized_body_bytes_are_pinned_across_library_upgrades()
    {
        var mime = MimeMessage.Load(new MemoryStream(Encoding.ASCII.GetBytes(PinnedRaw)));

        MessageBodyHasher.Hash(mime).ShouldBe(
            PinnedHash,
            "MimeKit's body serialization changed. Every stored content_hash is now stale, so "
            + "the next scan that re-parses a message will read it as a body change and re-embed "
            + "the corpus. Take that deliberately or hold the version — don't just update the constant.");
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
