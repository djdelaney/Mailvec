using Mailvec.Core.Parsing;

namespace Mailvec.Core.Tests.Parsing;

public class MessageParserTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Parsing", "Fixtures", name);

    private readonly MessageParser _parser = new();

    [Fact]
    public void Parses_plain_text_message()
    {
        var parsed = _parser.ParseFile(Fixture("plain-text.eml"));

        parsed.MessageId.ShouldBe("plain-001@example.com");
        parsed.ThreadId.ShouldBe("plain-001@example.com");
        parsed.Subject.ShouldBe("Lunch on Friday?");
        parsed.FromAddress.ShouldBe("alice@example.com");
        parsed.FromName.ShouldBe("Alice Example");
        parsed.ToAddresses.Single().Address.ShouldBe("bob@example.com");
        parsed.BodyText.ShouldNotBeNull().ShouldContain("ramen place");
        parsed.BodyHtml.ShouldBeNull();
        parsed.HasAttachments.ShouldBeFalse();
    }

    [Fact]
    public void Resolves_thread_id_from_References_header()
    {
        var parsed = _parser.ParseFile(Fixture("multipart.eml"));

        parsed.MessageId.ShouldBe("multi-002@example.com");
        parsed.ThreadId.ShouldBe("plain-001@example.com");
        parsed.ToAddresses.Single().Name.ShouldBe("Alice Example");
        parsed.CcAddresses.Single().Address.ShouldBe("carol@example.com");
        parsed.BodyText.ShouldNotBeNull().ShouldContain("Friday at 12:30");

        var html = parsed.BodyHtml.ShouldNotBeNull();
        html.ShouldContain("<p>");
    }

    [Fact]
    public void Strips_html_to_text_when_only_html_body_present()
    {
        var parsed = _parser.ParseFile(Fixture("html-only.eml"));

        parsed.BodyHtml.ShouldNotBeNull();
        var text = parsed.BodyText.ShouldNotBeNull();
        text.ShouldNotBeNullOrWhiteSpace();
        text.ShouldContain("vector search");
        text.ShouldContain("local-first");
        text.ShouldNotContain("<p>");
        text.ShouldNotContain("track(");        // script content stripped
        text.ShouldNotContain("font: 14px");    // style content stripped
    }

    [Fact]
    public void Decodes_rfc2047_encoded_headers_and_utf8_body()
    {
        var parsed = _parser.ParseFile(Fixture("unicode-headers.eml"));

        parsed.FromName.ShouldBe("青木");
        parsed.ToAddresses.Single().Name.ShouldBe("André");
        parsed.Subject.ShouldNotBeNull().ShouldContain("ユニコード");
        parsed.BodyText.ShouldNotBeNull().ShouldContain("カタカナ");
    }

    [Fact]
    public void Detects_attachments()
    {
        var parsed = _parser.ParseFile(Fixture("with-attachment.eml"));

        parsed.HasAttachments.ShouldBeTrue();
        parsed.BodyText.ShouldNotBeNull().ShouldContain("Quote is attached");
    }
}
