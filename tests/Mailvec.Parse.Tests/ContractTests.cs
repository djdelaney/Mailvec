using System.Text.Json;
using Mailvec.Parsing.Contracts;

namespace Mailvec.Parse.Tests;

/// <summary>
/// The wire must be invisible: every IMailParser operation answers the same
/// thing through the parse host as it does in-process, for the same input.
/// Records are compared through their JSON form (ParsedMessage holds lists,
/// so record equality would be reference equality); bytes are compared as
/// bytes — same library, same input, same process, so the rasteriser output
/// is deterministic.
/// </summary>
public class ContractTests : IAsyncLifetime
{
    private ParseHostFixture _host = null!;
    private readonly IMailParser _inProcess = Reference.InProcess();

    public async Task InitializeAsync() => _host = await ParseHostFixture.StartAsync();
    public async Task DisposeAsync() => await _host.DisposeAsync();

    public static TheoryData<string> PdfFixtures => ["text-sample.pdf", "digital-table-sample.pdf", "scanned-sample.pdf"];

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ParserWire.Json);

    [Theory]
    [MemberData(nameof(PdfFixtures))]
    public void ParseMessage_with_extraction_matches_in_process(string pdf)
    {
        var eml = Eml.Build(html: "<p>Please see the <b>attached</b>.</p>", text: null,
            new Eml.Part(pdf, "application/pdf", Eml.Fixture(pdf)));

        Json(_host.Remote.ParseMessage(eml, extractAttachmentText: true))
            .ShouldBe(Json(_inProcess.ParseMessage(eml, extractAttachmentText: true)));
    }

    [Fact]
    public void ParseMessage_without_extraction_matches_in_process_including_inline_images()
    {
        var eml = Eml.Build(html: "<p>photo <img src=\"cid:x\"></p>", text: null,
            new Eml.Part("scan.pdf", "application/pdf", Eml.Fixture("text-sample.pdf")),
            new Eml.Part("photo.png", "image/png", Eml.Png(64, 40), Inline: true));

        var remote = _host.Remote.ParseMessage(eml, extractAttachmentText: false);
        Json(remote).ShouldBe(Json(_inProcess.ParseMessage(eml, extractAttachmentText: false)));
        remote.Attachments.Count.ShouldBe(2);
        remote.Attachments[0].ExtractionStatus.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(PdfFixtures))]
    public void ExtractAttachmentText_matches_in_process(string pdf)
    {
        var eml = Eml.Build(parts: new Eml.Part(pdf, "application/pdf", Eml.Fixture(pdf)));

        _host.Remote.ExtractAttachmentText(eml, 0).ShouldBe(_inProcess.ExtractAttachmentText(eml, 0));
    }

    [Fact]
    public void DescribePart_and_DecodePart_match_in_process()
    {
        var bytes = Eml.Fixture("digital-table-sample.pdf");
        // octet-stream on purpose: the sniffed content type must survive the wire.
        var eml = Eml.Build(parts: new Eml.Part("table.pdf", "application/octet-stream", bytes));

        _host.Remote.DescribePart(eml, 0).ShouldBe(_inProcess.DescribePart(eml, 0));
        _host.Remote.DescribePart(eml, 0).ContentType.ShouldBe("application/pdf");

        var remote = _host.Remote.DecodePart(eml, 0, maxBytes: null);
        var local = _inProcess.DecodePart(eml, 0, maxBytes: null);
        remote.FileName.ShouldBe(local.FileName);
        remote.ContentType.ShouldBe(local.ContentType);
        remote.Bytes.ShouldBe(local.Bytes);
        remote.Bytes.ShouldBe(bytes);
    }

    [Theory]
    [MemberData(nameof(PdfFixtures))]
    public void RenderPdfPages_matches_in_process(string pdf)
    {
        var eml = Eml.Build(parts: new Eml.Part(pdf, "application/pdf", Eml.Fixture(pdf)));

        var remote = _host.Remote.RenderPdfPages(eml, 0, firstPage: 0, maxPages: 3, maxBytes: null);
        var local = _inProcess.RenderPdfPages(eml, 0, firstPage: 0, maxPages: 3, maxBytes: null);

        remote.PageCount.ShouldBe(local.PageCount);
        remote.Pages.Count.ShouldBe(local.Pages.Count);
        remote.Pages.Count.ShouldBeGreaterThan(0);
        for (int i = 0; i < remote.Pages.Count; i++)
            remote.Pages[i].ShouldBe(local.Pages[i]);
    }

    [Fact]
    public void RenderPdfPages_past_the_end_yields_no_pages_but_the_count()
    {
        var eml = Eml.Build(parts: new Eml.Part("t.pdf", "application/pdf", Eml.Fixture("text-sample.pdf")));

        var render = _host.Remote.RenderPdfPages(eml, 0, firstPage: 50, maxPages: 1, maxBytes: null);
        render.PageCount.ShouldBe(1);
        render.Pages.ShouldBeEmpty();
    }

    [Fact]
    public void NormalizeImage_matches_in_process_and_null_round_trips()
    {
        var eml = Eml.Build(parts:
        [
            new Eml.Part("photo.png", "image/png", Eml.Png(300, 200)),
            new Eml.Part("noise.png", "image/png", new byte[512]),
        ]);

        var remote = _host.Remote.NormalizeImage(eml, 0, maxBytes: null).ShouldNotBeNull();
        var local = _inProcess.NormalizeImage(eml, 0, maxBytes: null).ShouldNotBeNull();
        remote.Width.ShouldBe(local.Width);
        remote.Height.ShouldBe(local.Height);
        remote.Jpeg.ShouldBe(local.Jpeg);

        _inProcess.NormalizeImage(eml, 1, maxBytes: null).ShouldBeNull();
        _host.Remote.NormalizeImage(eml, 1, maxBytes: null).ShouldBeNull();
    }

    [Fact]
    public void BodyTextFromHtml_matches_in_process()
    {
        const string html = "<html><body><p>Hi,</p><p>Thanks for the update.</p><blockquote>On Monday, someone wrote:<br>old stuff</blockquote></body></html>";

        _host.Remote.BodyTextFromHtml(html, "Re: update")
            .ShouldBe(_inProcess.BodyTextFromHtml(html, "Re: update"));
    }

    [Fact]
    public async Task Up_answers_and_reports_the_request_count()
    {
        using var client = new HttpClient { BaseAddress = new Uri(_host.BaseAddress) };
        var before = await client.GetStringAsync(ParserWire.Up.TrimStart('/'));
        before.ShouldContain("\"status\":\"ok\"");

        _host.Remote.DescribePart(Eml.Build(parts: new Eml.Part("a.txt", "text/plain", "x"u8.ToArray())), 0);

        var after = await client.GetStringAsync(ParserWire.Up.TrimStart('/'));
        after.ShouldContain("\"requests\":1");
    }

    [Fact]
    public void The_remote_parser_reports_its_mode()
    {
        _host.Remote.Mode.ShouldBe("remote");
        _inProcess.Mode.ShouldBe("inprocess");
    }
}
