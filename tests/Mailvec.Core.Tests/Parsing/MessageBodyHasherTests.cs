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
