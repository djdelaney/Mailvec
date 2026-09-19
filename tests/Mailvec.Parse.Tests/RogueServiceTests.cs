using System.Net;
using Mailvec.Parsing.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mailvec.Parse.Tests;

/// <summary>
/// The parse service is the process that eats attacker-chosen bytes, so the
/// client has to assume it can be made to answer anything. These tests stand
/// up a server that is NOT the parse host and check that the shipped client
/// (built through ParserHttp, exactly as the DI registration builds it) can
/// neither be turned against its callers nor talked into a document verdict.
/// </summary>
public class RogueServiceTests
{
    private static byte[] TextEml() =>
        Eml.Build(parts: new Eml.Part("memo.txt", "text/plain", "twelve bytes"u8.ToArray()));

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task A_redirect_is_refused_and_the_message_bytes_go_nowhere(int status)
    {
        // The review's P1. With automatic redirects on — HttpClient's default
        // — a 307/308 from the service made the caller resend the POST body,
        // the whole .eml, to whatever Location the service named; the embedder
        // and mcp have the LAN egress the parse container was denied. The
        // "sink" here stands in for that destination.
        await using var sink = await RogueServer.StartAsync(async ctx => { ctx.Response.StatusCode = 200; await ctx.Response.WriteAsync("{}"); });
        await using var rogue = await RogueServer.StartAsync(ctx =>
        {
            ctx.Response.StatusCode = status;
            ctx.Response.Headers.Location = sink.BaseAddress + "exfil";
            return Task.CompletedTask;
        });
        await using var host = await ParseHostFixture.StartAsync();
        var remote = host.RemoteFor(rogue.BaseAddress);

        var ex = Should.Throw<ParseException>(() => remote.DescribePart(TextEml(), 0));

        ex.Kind.ShouldBe(ParseFailureKind.Crashed, "a redirecting service is not our service; retry with strikes, never retire");
        ex.Message.ShouldContain("redirect");
        sink.Requests.ShouldBe(0, "not one byte of the message may reach the redirect target");
        rogue.Requests.ShouldBe(1);
    }

    [Fact]
    public async Task A_success_body_that_is_not_a_parse_result_is_Crashed_not_a_document_verdict()
    {
        // Before: JsonException escaped unclassified, and every caller reads
        // a non-ParseException as "the parser opened the document and said
        // no" — a permanent `failed` stamp on a healthy attachment because a
        // proxy answered with an HTML page.
        await using var rogue = await RogueServer.StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html";
            await ctx.Response.WriteAsync("<html><body>Service Unavailable</body></html>");
        });
        await using var host = await ParseHostFixture.StartAsync();

        var ex = Should.Throw<ParseException>(() => host.RemoteFor(rogue.BaseAddress).DescribePart(TextEml(), 0));

        ex.Kind.ShouldBe(ParseFailureKind.Crashed);
        ex.InnerException.ShouldBeAssignableTo<System.Text.Json.JsonException>();
    }

    // ---- Follow-up F2: incomplete-but-valid JSON is not a parse result ----

    [Fact]
    public async Task An_empty_object_is_not_a_body_text_result()
    {
        // `{}` used to deserialize to HtmlResponse(Text: null), which
        // rebuild-bodies then wrote over the real body_text — silent data
        // loss from a service that answered nothing.
        await using var rogue = await RogueServer.StartAsync(async ctx => { ctx.Response.ContentType = "application/json"; await ctx.Response.WriteAsync("{}"); });
        await using var host = await ParseHostFixture.StartAsync();

        var ex = Should.Throw<ParseException>(() => host.RemoteFor(rogue.BaseAddress).BodyTextFromHtml("<p>x</p>", null));

        ex.Kind.ShouldBe(ParseFailureKind.Crashed);
    }

    [Fact]
    public async Task An_explicit_null_on_a_nullable_member_is_still_a_valid_answer()
    {
        // The other half of required/nullable enforcement: HtmlResponse.Text
        // IS nullable, and "no text" is a real answer the host gives.
        await using var rogue = await RogueServer.StartAsync(async ctx => { ctx.Response.ContentType = "application/json"; await ctx.Response.WriteAsync("{\"text\":null}"); });
        await using var host = await ParseHostFixture.StartAsync();

        host.RemoteFor(rogue.BaseAddress).BodyTextFromHtml("<p></p>", null).ShouldBeNull();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"fileName\":\"a.bin\",\"contentType\":\"application/octet-stream\"}")]          // bytes missing
    [InlineData("{\"fileName\":\"a.bin\",\"contentType\":\"application/octet-stream\",\"bytes\":null}")] // bytes null
    public async Task A_decoded_part_without_its_bytes_is_Crashed(string body)
    {
        await using var rogue = await RogueServer.StartAsync(async ctx => { ctx.Response.ContentType = "application/json"; await ctx.Response.WriteAsync(body); });
        await using var host = await ParseHostFixture.StartAsync();

        Should.Throw<ParseException>(() => host.RemoteFor(rogue.BaseAddress).DecodePart(TextEml(), 0, null))
            .Kind.ShouldBe(ParseFailureKind.Crashed);
    }

    [Fact]
    public async Task More_pages_than_were_requested_is_Crashed()
    {
        await using var rogue = await RogueServer.StartAsync(async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"pageCount\":3,\"pages\":[\"AAAA\",\"AAAA\",\"AAAA\"]}");
        });
        await using var host = await ParseHostFixture.StartAsync();

        Should.Throw<ParseException>(() => host.RemoteFor(rogue.BaseAddress).RenderPdfPages(TextEml(), 0, 0, maxPages: 1, maxBytes: null))
            .Kind.ShouldBe(ParseFailureKind.Crashed);
    }

    // ---- Follow-up F1: a compact response must not expand into gigabytes ----

    [Fact]
    public async Task A_compact_response_with_an_oversized_collection_is_refused_before_deserialization()
    {
        // 100,000 empty attachment objects: ~300 KB on the wire, well under the
        // byte ceiling, ~15 MB once deserialized. Refused by the shape scan;
        // ResponseShapeTests pins that the refusal itself allocates nothing.
        var body = "{\"attachments\":[" + string.Join(",", Enumerable.Repeat("{}", 100_000)) + "]}";
        await using var rogue = await RogueServer.StartAsync(async ctx => { ctx.Response.ContentType = "application/json"; await ctx.Response.WriteAsync(body); });
        await using var host = await ParseHostFixture.StartAsync();

        var ex = Should.Throw<ParseException>(() => host.RemoteFor(rogue.BaseAddress).ParseMessage(TextEml(), extractAttachmentText: false));

        ex.Kind.ShouldBe(ParseFailureKind.Crashed);
        ex.Message.ShouldContain("array longer than");
    }

    [Fact]
    public async Task An_unexpected_status_is_Crashed_not_a_document_verdict()
    {
        // A 4xx the wire contract does not define is a deployment bug (wrong
        // endpoint, mismatched versions), never a property of the document.
        await using var rogue = await RogueServer.StartAsync(ctx => { ctx.Response.StatusCode = 418; return Task.CompletedTask; });
        await using var host = await ParseHostFixture.StartAsync();

        var ex = Should.Throw<ParseException>(() => host.RemoteFor(rogue.BaseAddress).DescribePart(TextEml(), 0));

        ex.Kind.ShouldBe(ParseFailureKind.Crashed);
        ex.Message.ShouldContain("418");
    }

    [Fact]
    public async Task A_response_over_the_ceiling_is_refused_while_it_is_read()
    {
        // The service's own memory limit bounds nothing in the caller. A body
        // streamed past ParserOptions.MaxResponseBytes must be cut off by the
        // client, not buffered until the archive-holding process is OOM-killed.
        const long ceiling = 256 * 1024;
        var chunk = new byte[64 * 1024];
        Array.Fill(chunk, (byte)'x');
        await using var rogue = await RogueServer.StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            // Chunked, no Content-Length: the declared-length shortcut cannot
            // help, the client has to enforce the ceiling as it reads.
            for (var i = 0; i < 64; i++) // 4 MB, sixteen times the ceiling
            {
                try { await ctx.Response.Body.WriteAsync(chunk); }
                catch (Exception) { return; } // the client hung up — the point
            }
        });
        await using var host = await ParseHostFixture.StartAsync();

        var ex = Should.Throw<ParseException>(() =>
            host.RemoteFor(rogue.BaseAddress, maxResponseBytes: ceiling).DescribePart(TextEml(), 0));

        ex.Kind.ShouldBe(ParseFailureKind.Crashed);
    }

    [Fact]
    public async Task A_declared_length_over_the_ceiling_is_refused_before_the_body_is_read()
    {
        const long ceiling = 256 * 1024;
        await using var rogue = await RogueServer.StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength = 4L * 1024 * 1024;
            try { await ctx.Response.Body.WriteAsync(new byte[1024]); }
            catch (Exception) { /* client gone */ }
        });
        await using var host = await ParseHostFixture.StartAsync();

        var ex = Should.Throw<ParseException>(() =>
            host.RemoteFor(rogue.BaseAddress, maxResponseBytes: ceiling).DescribePart(TextEml(), 0));

        ex.Kind.ShouldBe(ParseFailureKind.Crashed);
    }
}

/// <summary>A Kestrel listener on a random loopback port that answers with whatever the test says.</summary>
public sealed class RogueServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _requests;

    public string BaseAddress { get; private set; } = "";
    public int Requests => Volatile.Read(ref _requests);

    private RogueServer(WebApplication app) => _app = app;

    public static async Task<RogueServer> StartAsync(Func<HttpContext, Task> respond)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new RogueServer(app);
        app.Run(async ctx =>
        {
            Interlocked.Increment(ref server._requests);
            await respond(ctx);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.BaseAddress = address.TrimEnd('/') + "/";
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); }
        catch (Exception) { /* already stopping */ }
        await _app.DisposeAsync();
    }
}
