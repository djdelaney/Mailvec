using Mailvec.Core.Options;
using Mailvec.Core.Search;

namespace Mailvec.Core.Tests.Search;

public class WebmailLinkBuilderTests
{
    [Fact]
    public void Returns_null_when_message_id_is_null()
    {
        WebmailLinkBuilder.Build(null, new FastmailOptions { AccountId = "u12345678" })
            .ShouldBeNull();
    }

    [Fact]
    public void Returns_null_when_message_id_is_whitespace()
    {
        WebmailLinkBuilder.Build("   ", new FastmailOptions { AccountId = "u12345678" })
            .ShouldBeNull();
    }

    [Fact]
    public void Returns_null_when_account_id_is_empty()
    {
        WebmailLinkBuilder.Build("<abc@example.com>", new FastmailOptions { AccountId = "" })
            .ShouldBeNull();
    }

    [Fact]
    public void Returns_null_when_account_id_is_whitespace()
    {
        WebmailLinkBuilder.Build("<abc@example.com>", new FastmailOptions { AccountId = "   " })
            .ShouldBeNull();
    }

    [Fact]
    public void Builds_link_with_default_web_url()
    {
        var url = WebmailLinkBuilder.Build("<abc@example.com>", new FastmailOptions { AccountId = "u12345678" });
        url.ShouldBe("https://app.fastmail.com/mail/search:msgid:%3Cabc%40example.com%3E?u=u12345678");
    }

    [Fact]
    public void Url_escapes_message_id_special_chars()
    {
        // '+' must be percent-encoded so Fastmail's URL parser doesn't read it as a space.
        var url = WebmailLinkBuilder.Build("<a+b/c@host>", new FastmailOptions { AccountId = "u1" });
        url.ShouldNotBeNull();
        url.ShouldContain("%2B");
        url.ShouldContain("%2F");
        url.ShouldNotContain("+");
    }

    [Fact]
    public void Url_escapes_account_id()
    {
        var url = WebmailLinkBuilder.Build("<x@y>", new FastmailOptions { AccountId = "u 12" });
        url!.ShouldEndWith("?u=u%2012");
    }

    [Fact]
    public void Custom_web_url_strips_trailing_slash()
    {
        var url = WebmailLinkBuilder.Build(
            "<x@y>",
            new FastmailOptions { AccountId = "u1", WebUrl = "https://mail.example.com/" });
        url.ShouldBe("https://mail.example.com/mail/search:msgid:%3Cx%40y%3E?u=u1");
    }

    [Fact]
    public void Null_web_url_falls_back_to_default()
    {
        var url = WebmailLinkBuilder.Build(
            "<x@y>",
            new FastmailOptions { AccountId = "u1", WebUrl = null! });
        url.ShouldStartWith("https://app.fastmail.com/mail/search:msgid:");
    }
}
