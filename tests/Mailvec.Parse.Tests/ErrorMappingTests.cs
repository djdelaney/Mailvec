using Mailvec.Core.Attachments;
using Mailvec.Parsing.Contracts;

namespace Mailvec.Parse.Tests;

/// <summary>
/// The classification the callers depend on. In-process exception types
/// survive the wire where the MCP tools and the OCR pass branch on them
/// (part out of range, attachment too large); everything else becomes a
/// ParseException whose kind says retire / strike / wait — and the two
/// host-initiated exits (timeout, request budget) actually stop the host.
/// </summary>
public class ErrorMappingTests
{
    private static byte[] TextEml() =>
        Eml.Build(parts: new Eml.Part("memo.txt", "text/plain", "twelve bytes"u8.ToArray()));

    [Fact]
    public async Task A_part_index_out_of_range_is_the_same_exception_in_process_throws()
    {
        await using var host = await ParseHostFixture.StartAsync();

        var ex = Should.Throw<ArgumentOutOfRangeException>(() => host.Remote.DescribePart(TextEml(), 5));
        ex.Message.ShouldContain("partIndex 5 is out of range");
        ex.ParamName.ShouldBe("partIndex");
    }

    [Fact]
    public async Task A_part_over_the_ceiling_is_AttachmentTooLarge_with_the_limit()
    {
        await using var host = await ParseHostFixture.StartAsync();

        var ex = Should.Throw<AttachmentTooLargeException>(() => host.Remote.DecodePart(TextEml(), 0, maxBytes: 4));
        ex.LimitBytes.ShouldBe(4);
        ex.Describe.ShouldBe("'memo.txt'");
        ex.Message.ShouldBe(new AttachmentTooLargeException("'memo.txt'", 4).Message);
    }

    [Fact]
    public async Task A_document_the_parser_refuses_is_DocumentRejected()
    {
        await using var host = await ParseHostFixture.StartAsync();
        var eml = Eml.Build(parts: new Eml.Part("broken.pdf", "application/pdf", "%PDF-1.4 not really"u8.ToArray()));

        var ex = Should.Throw<ParseException>(() => host.Remote.RenderPdfPages(eml, 0, 0, 1, maxBytes: null));
        ex.Kind.ShouldBe(ParseFailureKind.DocumentRejected);
    }

    [Fact]
    public async Task An_unreachable_service_is_Unavailable_not_a_strike()
    {
        await using var host = await ParseHostFixture.StartAsync();
        var nobody = host.RemoteFor("http://127.0.0.1:1/", timeoutSeconds: 5);

        var ex = Should.Throw<ParseException>(() => nobody.DescribePart(TextEml(), 0));
        ex.Kind.ShouldBe(ParseFailureKind.Unavailable);
    }

    [Fact]
    public async Task A_message_over_the_request_cap_is_DocumentRejected()
    {
        await using var host = await ParseHostFixture.StartAsync(configure: o => o.MaxRequestBodyBytes = 2048);
        var eml = Eml.Build(parts: new Eml.Part("big.bin", "application/octet-stream", new byte[16 * 1024]));

        var ex = Should.Throw<ParseException>(() => host.Remote.DescribePart(eml, 0));
        ex.Kind.ShouldBe(ParseFailureKind.DocumentRejected);
        ex.Message.ShouldContain("larger");
    }

    [Fact]
    public async Task A_timed_out_parse_is_a_Crashed_strike_and_the_host_exits()
    {
        await using var host = await ParseHostFixture.StartAsync(
            parser: new SlowParser(TimeSpan.FromSeconds(20)),
            configure: o => o.RequestTimeoutSeconds = 1);

        var ex = Should.Throw<ParseException>(() => host.Remote.DescribePart(TextEml(), 0));
        ex.Kind.ShouldBe(ParseFailureKind.Crashed);
        ex.Message.ShouldContain("timeout");

        (await host.WaitForStopAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue(
            "the host must exit after answering 504: the parse thread can only be reclaimed by ending the process");
    }

    [Fact]
    public async Task The_request_budget_stops_the_host_after_the_configured_count()
    {
        await using var host = await ParseHostFixture.StartAsync(configure: o => o.MaxRequestsBeforeExit = 2);

        host.Remote.DescribePart(TextEml(), 0);
        host.StopRequested.ShouldBeFalse();
        host.Remote.DescribePart(TextEml(), 0);

        (await host.WaitForStopAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
    }

    [Fact]
    public async Task A_budget_of_zero_never_stops_the_host()
    {
        await using var host = await ParseHostFixture.StartAsync(configure: o => o.MaxRequestsBeforeExit = 0);

        for (int i = 0; i < 5; i++) host.Remote.DescribePart(TextEml(), 0);

        host.StopRequested.ShouldBeFalse();
    }
}
