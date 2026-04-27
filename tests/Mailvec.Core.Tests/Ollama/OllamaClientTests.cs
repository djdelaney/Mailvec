using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mailvec.Core.Tests.Ollama;

public class OllamaClientTests
{
    private static OllamaClient ClientWith(Func<HttpRequestMessage, HttpResponseMessage> respond, OllamaOptions? opts = null)
    {
        var http = new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://localhost:11434") };
        return new OllamaClient(
            http,
            Microsoft.Extensions.Options.Options.Create(opts ?? new OllamaOptions { EmbeddingDimensions = 4 }),
            NullLogger<OllamaClient>.Instance);
    }

    [Fact]
    public async Task Sends_input_array_and_returns_vectors_in_order()
    {
        HttpRequestMessage? captured = null;
        var client = ClientWith(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { model = "test", embeddings = new[]
                {
                    new[] { 1f, 0f, 0f, 0f },
                    new[] { 0f, 1f, 0f, 0f },
                }})
            };
        });

        var result = await client.EmbedAsync(["alpha", "beta"]);

        result.Length.ShouldBe(2);
        result[0].ShouldBe(new[] { 1f, 0f, 0f, 0f });
        result[1].ShouldBe(new[] { 0f, 1f, 0f, 0f });

        captured.ShouldNotBeNull();
        var body = await captured.Content!.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("model").GetString().ShouldBe("mxbai-embed-large");
        doc.RootElement.GetProperty("input").EnumerateArray()
            .Select(e => e.GetString()).ShouldBe(new[] { "alpha", "beta" });
    }

    [Fact]
    public async Task Empty_input_short_circuits_without_calling_server()
    {
        var called = false;
        var client = ClientWith(_ => { called = true; return new HttpResponseMessage(HttpStatusCode.OK); });

        var result = await client.EmbedAsync([]);

        result.ShouldBeEmpty();
        called.ShouldBeFalse();
    }

    [Fact]
    public async Task Throws_when_server_returns_wrong_count()
    {
        var client = ClientWith(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { embeddings = new[] { new[] { 1f, 2f, 3f, 4f } } })
        });

        await Should.ThrowAsync<InvalidOperationException>(
            () => client.EmbedAsync(["one", "two"]));
    }

    [Fact]
    public async Task Throws_when_dimensions_mismatch_configured_model()
    {
        var client = ClientWith(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // 3 dims, but configured for 4
            Content = JsonContent.Create(new { embeddings = new[] { new[] { 1f, 2f, 3f } } })
        });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => client.EmbedAsync(["x"]));
        ex.Message.ShouldContain("3 dimensions");
        ex.Message.ShouldContain("4");
    }

    [Fact]
    public async Task Surfaces_http_error_status()
    {
        var client = ClientWith(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("model not found")
        });

        await Should.ThrowAsync<HttpRequestException>(() => client.EmbedAsync(["x"]));
    }

    [Fact]
    public async Task Non_context_400_propagates_without_retry()
    {
        // 400 with a different error message should NOT trigger the truncation fallback.
        var calls = 0;
        var client = ClientWith(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"model not loaded\"}"),
            };
        });

        await Should.ThrowAsync<HttpRequestException>(() => client.EmbedAsync(["x"]));
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task Splits_batch_on_context_length_400_and_succeeds_when_split_inputs_fit()
    {
        // First call: batch of 2 returns context-length 400.
        // Subsequent singleton calls: succeed.
        var calls = 0;
        var client = ClientWith(req =>
        {
            calls++;
            if (calls == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"the input length exceeds the context length\"}"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { embeddings = new[] { new[] { 1f, 2f, 3f, 4f } } }),
            };
        });

        var result = await client.EmbedAsync(["short", "alsoshort"]);

        result.Length.ShouldBe(2);
        calls.ShouldBe(3);   // initial batch failed, then two singletons
    }

    [Fact]
    public async Task Truncates_singleton_progressively_on_context_length_400()
    {
        // First N calls return context-length 400; eventually a short-enough input succeeds.
        var calls = 0;
        var lastSentInput = string.Empty;
        var client = ClientWith(req =>
        {
            calls++;
            // Capture body to check we're truncating.
            var body = req.Content!.ReadAsStringAsync().Result;
            using var doc = JsonDocument.Parse(body);
            lastSentInput = doc.RootElement.GetProperty("input")[0].GetString() ?? string.Empty;

            // Pretend anything over 200 chars is too long.
            if (lastSentInput.Length > 200)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"the input length exceeds the context length\"}"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { embeddings = new[] { new[] { 1f, 2f, 3f, 4f } } }),
            };
        });

        var huge = new string('x', 2000);
        var result = await client.EmbedAsync([huge]);

        result.Length.ShouldBe(1);
        lastSentInput.Length.ShouldBeLessThanOrEqualTo(200);   // last accepted size fit the stub's pretend limit
        calls.ShouldBeGreaterThan(1);                          // at least one truncation happened
    }

    [Fact]
    public async Task Throws_when_truncation_floor_reached_without_success()
    {
        // Stub always returns context-length 400 — even tiny inputs "don't fit".
        var calls = 0;
        var client = ClientWith(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"the input length exceeds the context length\"}"),
            };
        });

        var huge = new string('x', 2000);
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => client.EmbedAsync([huge]));
        ex.Message.ShouldContain("truncation");
        calls.ShouldBeGreaterThan(3);   // at least: initial + several truncation halvings
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
