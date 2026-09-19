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
    public async Task A_message_over_the_request_cap_is_DocumentRejected_without_being_sent()
    {
        // The caller mirrors the host's cap (Parser:MaxRequestBodyBytes) and
        // refuses before a byte goes on the wire — pointed at a closed port,
        // so a network call would fail as Unavailable, not DocumentRejected.
        await using var host = await ParseHostFixture.StartAsync();
        var nobody = host.RemoteFor("http://127.0.0.1:1/", timeoutSeconds: 5, maxRequestBodyBytes: 2048);
        var eml = Eml.Build(parts: new Eml.Part("big.bin", "application/octet-stream", new byte[16 * 1024]));

        var ex = Should.Throw<ParseException>(() => nobody.DescribePart(eml, 0));

        ex.Kind.ShouldBe(ParseFailureKind.DocumentRejected);
        ex.Message.ShouldContain("larger");
    }

    [Fact]
    public async Task A_message_over_the_host_cap_but_under_the_mirror_is_still_DocumentRejected()
    {
        // The fourth review's reproduction: 8 MB against a 4 MB host cap. The
        // old test used 16 KB against 2 KB, which passes only because a small
        // body lands in the socket buffer before Kestrel's 413 arrives; at
        // production sizes the upload raced a mid-body reset and the send
        // failed as Crashed — a strike, then a degraded index with every
        // attachment stamped failed, for a message that was merely large.
        // Expect: 100-continue makes the host refuse at the headers.
        await using var host = await ParseHostFixture.StartAsync(configure: o => o.MaxRequestBodyBytes = 4L * 1024 * 1024);
        var remote = host.RemoteFor(host.BaseAddress, maxRequestBodyBytes: 64L * 1024 * 1024); // a drifted, too-generous mirror
        var eml = Eml.Build(parts: new Eml.Part("big.bin", "application/octet-stream", new byte[8 * 1024 * 1024]));

        var ex = Should.Throw<ParseException>(() => remote.DescribePart(eml, 0));

        ex.Kind.ShouldBe(ParseFailureKind.DocumentRejected);
        ex.Message.ShouldContain("larger");
    }

    [Fact]
    public async Task A_caller_that_disconnects_mid_parse_does_not_take_the_host_down()
    {
        // The fourth review's F2: RequestAborted used to share the timeout
        // branch, so `docker compose stop indexer` mid-parse, or a cancelled
        // tool call, answered 504 to a dead connection and EXITED — a restart
        // for every other caller. The parse is left to finish within its own
        // timeout; only a genuine overrun exits.
        await using var host = await ParseHostFixture.StartAsync(
            parser: new SlowParser(TimeSpan.FromSeconds(3)),
            configure: o => o.RequestTimeoutSeconds = 30);
        var impatient = host.RemoteFor(host.BaseAddress, timeoutSeconds: 1);

        Should.Throw<ParseException>(() => impatient.DescribePart(TextEml(), 0)).Kind.ShouldBe(ParseFailureKind.Unavailable);

        (await host.WaitForStopAsync(TimeSpan.FromSeconds(5))).ShouldBeFalse("a client disconnect is not a parse overrun");
        // …and the host is still serving.
        host.Remote.DescribePart(TextEml(), 0).FileName.ShouldBe("slow.bin");
    }

    [Fact]
    public async Task Parses_beyond_the_concurrency_limit_wait_for_a_slot()
    {
        // The fourth review's F3: no admission control meant every accepted
        // connection started an unbounded parse inside a 2 GB container. With
        // one slot, two parallel parses serialize.
        await using var host = await ParseHostFixture.StartAsync(
            parser: new SlowParser(TimeSpan.FromSeconds(1)),
            configure: o => { o.MaxConcurrentParses = 1; o.RequestTimeoutSeconds = 30; });
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var results = await Task.WhenAll(
            Task.Run(() => host.Remote.DescribePart(TextEml(), 0)),
            Task.Run(() => host.Remote.DescribePart(TextEml(), 0)));

        sw.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(1.9), "one slot: the second parse waited for the first");
        results.ShouldAllBe(p => p.FileName == "slow.bin");
    }

    [Fact]
    public async Task A_parse_that_cannot_get_a_slot_within_the_timeout_is_Unavailable_not_a_strike()
    {
        // One slot, two-second parses, a one-second timeout for the slot wait:
        // the first parse holds the slot past the third caller's wait. 503 →
        // Unavailable, which the callers wait out. Never Crashed: the service
        // being full says nothing about the document.
        await using var host = await ParseHostFixture.StartAsync(
            parser: new SlowParser(TimeSpan.FromSeconds(2)),
            configure: o => { o.MaxConcurrentParses = 1; o.RequestTimeoutSeconds = 3; });
        var quick = host.RemoteFor(host.BaseAddress, timeoutSeconds: 30);

        var first = Task.Run(() => host.Remote.DescribePart(TextEml(), 0));
        await Task.Delay(200); // let it take the slot
        var second = Task.Run(() => host.Remote.DescribePart(TextEml(), 0));  // waits ≤3 s, gets the slot at ~2 s
        await Task.Delay(200);
        var ex = Should.Throw<ParseException>(() => quick.DescribePart(TextEml(), 0)); // waits ≤3 s; slot busy until ~4 s

        ex.Kind.ShouldBe(ParseFailureKind.Unavailable);
        ex.Message.ShouldContain("concurrency");
        await Task.WhenAll(first, second);
        host.StopRequested.ShouldBeFalse();
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
