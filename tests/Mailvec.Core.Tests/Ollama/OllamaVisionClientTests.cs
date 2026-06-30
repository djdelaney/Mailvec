using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mailvec.Core.Tests.Ollama;

public class OllamaVisionClientTests
{
    private static OllamaVisionClient ClientWith(Func<HttpRequestMessage, HttpResponseMessage> respond, OllamaOptions? opts = null)
    {
        var http = new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://localhost:11434") };
        return new OllamaVisionClient(
            http, Microsoft.Extensions.Options.Options.Create(opts ?? new OllamaOptions()),
            NullLogger<OllamaVisionClient>.Instance);
    }

    [Fact]
    public async Task OcrAsync_posts_image_to_generate_and_returns_the_transcription()
    {
        HttpRequestMessage? captured = null;
        var client = ClientWith(req => { captured = req; return Ok(new { response = "INVOICE TOTAL 1234" }); });

        var text = await client.OcrAsync([1, 2, 3]);

        text.ShouldBe("INVOICE TOTAL 1234");
        captured!.RequestUri!.AbsolutePath.ShouldBe("/api/generate");
        var root = JsonDocument.Parse(await captured.Content!.ReadAsStringAsync()).RootElement;
        root.GetProperty("model").GetString().ShouldBe("qwen2.5vl:7b");
        root.GetProperty("images").GetArrayLength().ShouldBe(1);
        root.GetProperty("stream").GetBoolean().ShouldBeFalse();
        root.GetProperty("options").GetProperty("temperature").GetDouble().ShouldBe(0);
    }

    [Fact]
    public async Task OcrAsync_throws_on_provider_error()
    {
        var client = ClientWith(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("model 'qwen2.5vl:7b' not found"),
        });
        await Should.ThrowAsync<HttpRequestException>(() => client.OcrAsync([1]));
    }

    [Fact]
    public async Task OcrAsync_returns_empty_string_when_response_is_missing()
    {
        var client = ClientWith(_ => Ok(new { }));
        (await client.OcrAsync([1])).ShouldBe(string.Empty);
    }

    [Fact]
    public async Task OcrAsync_omits_keep_alive_by_default_and_includes_it_when_configured()
    {
        HttpRequestMessage? def = null;
        await ClientWith(req => { def = req; return Ok(new { response = "x" }); }).OcrAsync([1]);
        JsonDocument.Parse(await def!.Content!.ReadAsStringAsync()).RootElement
            .TryGetProperty("keep_alive", out _).ShouldBeFalse(); // default empty -> omitted

        HttpRequestMessage? set = null;
        await ClientWith(req => { set = req; return Ok(new { response = "x" }); },
            new OllamaOptions { VisionKeepAlive = "5m" }).OcrAsync([1]);
        JsonDocument.Parse(await set!.Content!.ReadAsStringAsync()).RootElement
            .GetProperty("keep_alive").GetString().ShouldBe("5m");
    }

    [Theory]
    [InlineData("qwen2.5vl:7b", true)]   // exact
    [InlineData("qwen2.5vl", true)]      // tagless config matches any tag of the base
    [InlineData("llava", false)]         // not pulled
    public async Task IsModelAvailableAsync_checks_the_tags_list(string configured, bool expected)
    {
        var client = ClientWith(_ => Ok(new
        {
            models = new[] { new { name = "qwen2.5vl:7b" }, new { name = "mxbai-embed-large:latest" } },
        }), new OllamaOptions { VisionModel = configured });

        (await client.IsModelAvailableAsync()).ShouldBe(expected);
    }

    [Fact]
    public async Task IsModelAvailableAsync_returns_false_when_ollama_errors()
    {
        var client = ClientWith(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        (await client.IsModelAvailableAsync()).ShouldBeFalse();
    }

    private static HttpResponseMessage Ok(object body) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
