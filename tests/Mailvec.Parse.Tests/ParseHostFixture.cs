using System.Diagnostics;
using Mailvec.Core.Attachments;
using Mailvec.Core.Parsing;
using Mailvec.Parse;
using Mailvec.Parsing;
using Mailvec.Parsing.Contracts;
using Mailvec.Pdf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using MimeKit.Utils;
using SkiaSharp;

namespace Mailvec.Parse.Tests;

/// <summary>A running parse host on a random loopback port, plus a RemoteParser pointed at it.</summary>
public sealed class ParseHostFixture : IAsyncDisposable
{
    public WebApplication App { get; }
    public string BaseAddress { get; private set; } = "";
    public RemoteParser Remote { get; private set; } = null!;
    public bool StopRequested { get; private set; }

    private ParseHostFixture(WebApplication app) => App = app;

    public static async Task<ParseHostFixture> StartAsync(IMailParser? parser = null, Action<ParseHostOptions>? configure = null)
    {
        var app = ParseHost.Build([], parser, configure, url: "http://127.0.0.1:0");
        var fixture = new ParseHostFixture(app);
        app.Lifetime.ApplicationStopping.Register(() => fixture.StopRequested = true);
        await app.StartAsync();
        fixture.BaseAddress = ParseHost.BoundAddress(app).TrimEnd('/') + "/";
        fixture.Remote = fixture.RemoteFor(fixture.BaseAddress);
        return fixture;
    }

    /// <summary>A client for any base address — the unreachable-service test points one at a closed port.</summary>
    public RemoteParser RemoteFor(string baseAddress, int timeoutSeconds = 30) =>
        new(() => new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(timeoutSeconds) });

    public async Task<bool> WaitForStopAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!StopRequested && sw.Elapsed < timeout)
            await Task.Delay(25);
        return StopRequested;
    }

    public async ValueTask DisposeAsync()
    {
        try { await App.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); }
        catch (Exception) { /* already stopping */ }
        await App.DisposeAsync();
    }
}

/// <summary>The in-process reference every contract test compares the wire against.</summary>
public static class Reference
{
    public static InProcessParser InProcess() =>
        new(new InProcessParserSettings(ParseHostOptions.DefaultAttachmentMaxBytes), NullLoggerFactory.Instance);
}

/// <summary>Builds <c>.eml</c> bytes with MimeKit, plus small binary fixtures.</summary>
public static class Eml
{
    public record Part(string Name, string ContentType, byte[] Bytes, bool Inline = false);

    public static byte[] Build(string? html = null, string? text = "hello", params Part[] parts)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        msg.To.Add(new MailboxAddress("Recipient", "recipient@example.test"));
        msg.Subject = "Contract fixture";
        msg.MessageId = "fixture@example.test";
        msg.Date = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

        var body = new BodyBuilder { TextBody = text, HtmlBody = html };
        foreach (var p in parts)
        {
            if (p.Inline)
            {
                var res = body.LinkedResources.Add(p.Name, p.Bytes, MimeKit.ContentType.Parse(p.ContentType));
                res.ContentId = MimeUtils.GenerateMessageId();
            }
            else
            {
                body.Attachments.Add(p.Name, p.Bytes, MimeKit.ContentType.Parse(p.ContentType));
            }
        }
        msg.Body = body.ToMessageBody();

        using var ms = new MemoryStream();
        msg.WriteTo(ms);
        return ms.ToArray();
    }

    public static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static byte[] Png(int width, int height)
    {
        using var bmp = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.CornflowerBlue);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>An IMailParser whose DescribePart takes as long as you say — for the timeout test.</summary>
public sealed class SlowParser(TimeSpan delay) : IMailParser
{
    public string Mode => "slow";
    public PartInfo DescribePart(byte[] eml, int partIndex) { Thread.Sleep(delay); return new PartInfo("slow.bin", "application/octet-stream"); }
    public ParsedMessage ParseMessage(byte[] eml, bool extractAttachmentText) => throw new NotSupportedException();
    public ExtractionResult ExtractAttachmentText(byte[] eml, int partIndex) => throw new NotSupportedException();
    public DecodedPart DecodePart(byte[] eml, int partIndex, long? maxBytes) => throw new NotSupportedException();
    public PdfRender RenderPdfPages(byte[] eml, int partIndex, int firstPage, int maxPages, long? maxBytes) => throw new NotSupportedException();
    public NormalizedImage? NormalizeImage(byte[] eml, int partIndex, long? maxBytes) => throw new NotSupportedException();
    public string? BodyTextFromHtml(string html, string? subject) => throw new NotSupportedException();
}
