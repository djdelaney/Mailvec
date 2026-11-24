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
    public void Headers_are_not_carried_on_the_parse_result()
    {
        // This used to be mime.Headers.ToString(), which HeaderList doesn't
        // override: every stored row read "MimeKit.HeaderList". Headers are
        // read from the .eml on demand (get_email_headers) instead.
        var parsed = _parser.ParseFile(Fixture("plain-text.eml"));

        parsed.RawHeaders.ShouldBeEmpty();
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

    [Fact]
    public void Extracts_attachment_metadata()
    {
        var parsed = _parser.ParseFile(Fixture("with-attachment.eml"));

        var att = parsed.Attachments.ShouldHaveSingleItem();
        att.PartIndex.ShouldBe(0);
        att.FileName.ShouldBe("quote-2025.pdf");
        att.ContentType.ShouldBe("application/pdf");
        att.SizeBytes.ShouldNotBeNull().ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Plain_text_message_has_no_attachments()
    {
        var parsed = _parser.ParseFile(Fixture("plain-text.eml"));

        parsed.Attachments.ShouldBeEmpty();
        parsed.HasAttachments.ShouldBeFalse();
    }

    [Fact]
    public void Message_without_message_id_gets_stable_synthetic_id()
    {
        // Message-ID-less mail is rare but real (old imports, drafts, some
        // automated senders). Throwing made these permanently unsearchable
        // AND re-parsed (attachments re-extracted) on every 5-minute scan,
        // since the mtime fast-path needs a stored message_id.
        const string raw = """
            Date: Mon, 13 Jan 2025 10:15:00 -0500
            From: alice@example.com
            To: bob@example.com
            Subject: no message id here
            MIME-Version: 1.0
            Content-Type: text/plain; charset=utf-8

            body without an id

            """;

        var first = ParseRaw(raw);
        var second = ParseRaw(raw);

        first.MessageId.ShouldEndWith("@synthetic.mailvec.local");
        // Deterministic: the same file rescanned (or renamed by mbsync) maps
        // to the same id, so it upserts instead of duplicating.
        second.MessageId.ShouldBe(first.MessageId);
        first.ThreadId.ShouldBe(first.MessageId);

        // A different message (changed body) must get a different id.
        var other = ParseRaw(raw.Replace("body without an id", "different body"));
        other.MessageId.ShouldNotBe(first.MessageId);
        other.MessageId.ShouldEndWith("@synthetic.mailvec.local");
    }

    [Fact]
    public void Attachment_size_bytes_is_the_decoded_length_not_the_encoded_length()
    {
        // base64 inflates ~33%, so the encoded MIME stream length over-reported
        // the payload size. size_bytes must be the decoded length.
        var payload = new byte[90];                       // decoded size we assert
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
        var b64 = Convert.ToBase64String(payload);        // 120 chars encoded

        var raw =
            "Date: Mon, 13 Jan 2025 10:15:00 -0500\r\n" +
            "From: alice@example.com\r\n" +
            "To: bob@example.com\r\n" +
            "Subject: with base64 attachment\r\n" +
            "Message-ID: <b64@example.com>\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"BOUND\"\r\n\r\n" +
            "--BOUND\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n\r\nsee attachment\r\n" +
            "--BOUND\r\n" +
            "Content-Type: application/octet-stream; name=\"blob.bin\"\r\n" +
            "Content-Disposition: attachment; filename=\"blob.bin\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n\r\n" +
            b64 + "\r\n" +
            "--BOUND--\r\n";

        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(raw));
        var mime = MimeKit.MimeMessage.Load(stream);
        var parsed = _parser.Parse(mime, sizeBytes: raw.Length);

        var att = parsed.Attachments.ShouldHaveSingleItem();
        att.SizeBytes.ShouldBe(90);   // decoded; the encoded base64 would be 120
    }

    // Every other text source was capped (HTML input, each attachment); a
    // text/plain body was bounded only by the message size limit, so one large
    // text mail became tens of thousands of chunks in a single embed batch.
    [Fact]
    public void An_oversized_plain_text_body_is_cut_to_the_body_ceiling()
    {
        var lines = new System.Text.StringBuilder();
        for (int i = 0; lines.Length < MessageParser.MaxBodyTextChars + 200_000; i++)
            lines.Append("Paragraph ").Append(i).Append(" of an ordinary but very long message.\n");

        var parsed = ParseRaw(
            "From: a@example.com\nMessage-ID: <big@example.com>\nSubject: big\n" +
            "Content-Type: text/plain; charset=utf-8\n\n" + lines);

        var body = parsed.BodyText.ShouldNotBeNull();
        body.Length.ShouldBeLessThanOrEqualTo(MessageParser.MaxBodyTextChars);
        body.Length.ShouldBeGreaterThan(MessageParser.MaxBodyTextChars / 2); // cut, not dropped
        body.ShouldStartWith("Paragraph 0 ");
    }

    [Fact]
    public void An_oversized_html_body_is_cut_to_the_html_ceiling_and_still_yields_text()
    {
        var html = "<html><body><p>MAILVEC OPENING</p>" +
            string.Concat(Enumerable.Repeat("<p>filler paragraph text</p>", MessageParser.MaxBodyHtmlChars / 20)) +
            "</body></html>";
        html.Length.ShouldBeGreaterThan(MessageParser.MaxBodyHtmlChars);

        var parsed = ParseRaw(
            "From: a@example.com\nMessage-ID: <bightml@example.com>\nSubject: big\n" +
            "Content-Type: text/html; charset=utf-8\n\n" + html);

        parsed.BodyHtml.ShouldNotBeNull().Length.ShouldBeLessThanOrEqualTo(MessageParser.MaxBodyHtmlChars);
        parsed.BodyText.ShouldNotBeNull().ShouldContain("MAILVEC OPENING");
    }

    [Fact]
    public void The_body_cut_never_leaves_half_a_surrogate_pair()
    {
        var text = "ab\U0001F600cd"; // the emoji is chars 2 and 3

        MessageParser.CapChars(text, 3).ShouldBe("ab");
        MessageParser.CapChars(text, 4).ShouldBe("ab\U0001F600");
        MessageParser.CapChars(text, 100).ShouldBeSameAs(text);
        MessageParser.CapChars(null, 3).ShouldBeNull();
    }

    private ParsedMessage ParseRaw(string raw)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw.Replace("\r\n", "\n").Replace("\n", "\r\n")));
        var mime = MimeKit.MimeMessage.Load(stream);
        return _parser.Parse(mime, sizeBytes: raw.Length);
    }

    [Fact]
    public void Captures_inline_cid_image_as_an_attachment()
    {
        // Inline (Content-Disposition: inline / cid:) images are excluded from
        // MimeKit's mime.Attachments; MessageParts.Indexable pulls them in so
        // they get a row and flow into extraction/OCR.
        var parsed = _parser.ParseFile(Fixture("inline-image.eml"));

        parsed.HasAttachments.ShouldBeTrue();
        var att = parsed.Attachments.ShouldHaveSingleItem();
        att.PartIndex.ShouldBe(0);
        att.FileName.ShouldBe("photo.png");
        att.ContentType.ShouldBe("image/png");
    }

    // ---- Header length (MimeKit 5) ------------------------------------------
    //
    // MimeKit 5 added ParserOptions.MaxHeaderLength, 16 KB by default, and
    // throws FormatException past it. MimeKit 4 had no limit and real mail
    // carries longer fields, so at the default those messages stop parsing:
    // an indexed one on its next re-parse, a new one before it is ever
    // indexed. Both production parse paths must use MimeParsing.Options.

    private static byte[] MessageWithLongToHeader(int recipients)
    {
        var to = string.Join(",\r\n ", Enumerable.Range(0, recipients).Select(i => $"recipient{i:D5}@example.com"));
        var raw =
            "From: Alice <alice@example.com>\r\n" +
            $"To: {to}\r\n" +
            "Subject: long header\r\n" +
            "Message-Id: <long-header@example.com>\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "\r\n" +
            "body\r\n";
        return System.Text.Encoding.ASCII.GetBytes(raw);
    }

    [Fact]
    public void A_header_field_over_MimeKits_default_limit_still_parses_on_both_paths()
    {
        var bytes = MessageWithLongToHeader(1500);
        bytes.Length.ShouldBeGreaterThan(40_000);

        // The guard against a vacuous test: MimeKit's own default refuses it.
        Should.Throw<FormatException>(() => MimeKit.MimeMessage.Load(new MemoryStream(bytes)));

        var viaBytes = new Mailvec.Parsing.InProcessParser(extractor: null).ParseMessage(bytes, extractAttachmentText: false);
        viaBytes.MessageId.ShouldBe("long-header@example.com");
        viaBytes.ToAddresses.Count.ShouldBe(1500);

        var path = Path.Combine(Path.GetTempPath(), $"mailvec-long-header-{Guid.NewGuid():N}.eml");
        File.WriteAllBytes(path, bytes);
        try
        {
            var viaFile = _parser.ParseFile(path);
            viaFile.MessageId.ShouldBe("long-header@example.com");
            viaFile.ToAddresses.Count.ShouldBe(1500);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
