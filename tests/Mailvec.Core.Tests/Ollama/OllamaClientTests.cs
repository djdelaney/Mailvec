using Mailvec.Core.Embedding;
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
    public async Task Returns_raw_vectors_without_normalizing_or_width_checking()
    {
        // The transport contract: raw vectors through. Dimension validation,
        // finiteness and L2 normalization live in EmbeddingService — once for
        // every transport (see EmbeddingServiceTests).
        var client = ClientWith(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { embeddings = new[] { new[] { 2f, 0f, 0f } } })  // 3-wide, unnormalized
        });

        var result = await client.EmbedAsync(["x"]);

        result[0].ShouldBe(new[] { 2f, 0f, 0f });
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

        var ex = await Should.ThrowAsync<EmbeddingException>(
            () => client.EmbedAsync(["one", "two"]));
        ex.Kind.ShouldBe(EmbeddingFailureKind.InvalidResponse);
    }


    [Fact]
    public async Task Surfaces_http_error_status()
    {
        var client = ClientWith(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("model not found")
        });

        var ex = await Should.ThrowAsync<EmbeddingException>(() => client.EmbedAsync(["x"]));
        ex.Kind.ShouldBe(EmbeddingFailureKind.Transient); // other 5xx: bounded retry territory
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

        var ex = await Should.ThrowAsync<EmbeddingException>(() => client.EmbedAsync(["x"]));
        ex.Kind.ShouldBe(EmbeddingFailureKind.Transient);
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
        var ex = await Should.ThrowAsync<EmbeddingException>(() => client.EmbedAsync([huge]));
        ex.Kind.ShouldBe(EmbeddingFailureKind.InputTooLong);
        ex.Message.ShouldContain("truncation");
        calls.ShouldBeGreaterThan(3);   // at least: initial + several truncation halvings
    }

    // ---------- IsModelAvailableAsync (tri-state /api/tags probe) ----------

    private static HttpResponseMessage Tags(params string[] names) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { models = names.Select(n => new { name = n }).ToArray() }),
    };

    [Fact]
    public async Task Model_probe_true_when_listed_exactly_or_by_tag()
    {
        // Config default is "mxbai-embed-large"; Ollama lists it as ":latest".
        (await ClientWith(_ => Tags("mxbai-embed-large:latest")).IsModelAvailableAsync()).ShouldBe(true);
        (await ClientWith(_ => Tags("mxbai-embed-large")).IsModelAvailableAsync()).ShouldBe(true);
        (await ClientWith(_ => Tags("MXBAI-Embed-Large:latest")).IsModelAvailableAsync()).ShouldBe(true);
    }

    [Fact]
    public async Task Model_probe_false_when_server_answers_but_model_absent()
    {
        // false ≠ null: the server IS up, the model was never pulled. Doctor
        // keys its "run `ollama pull …`" advice off this exact value.
        (await ClientWith(_ => Tags("qwen2.5vl:7b")).IsModelAvailableAsync()).ShouldBe(false);
        (await ClientWith(_ => Tags()).IsModelAvailableAsync()).ShouldBe(false);
        // Base-name prefixing must not false-positive on a different model
        // that merely starts with the same string.
        (await ClientWith(_ => Tags("mxbai-embed-large-v2:latest")).IsModelAvailableAsync()).ShouldBe(false);
    }

    [Fact]
    public async Task Model_probe_null_when_server_unreachable()
    {
        var client = ClientWith(_ => throw new HttpRequestException("connection refused"));
        (await client.IsModelAvailableAsync()).ShouldBeNull();
    }

    // ---------- GetModelArtifactDigestAsync (/api/tags digest) ----------

    private static HttpResponseMessage TagsWithDigests(params (string Name, string? Digest)[] models) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                models = models.Select(m => new { name = m.Name, digest = m.Digest }).ToArray(),
            }),
        };

    [Fact]
    public async Task Digest_comes_from_the_matched_model_including_latest_tag_resolution()
    {
        var client = ClientWith(_ => TagsWithDigests(
            ("qwen2.5vl:7b", "sha256:vision"),
            ("mxbai-embed-large:latest", "sha256:embed")));
        (await client.GetModelArtifactDigestAsync()).ShouldBe("sha256:embed");
    }

    [Fact]
    public async Task Digest_resolves_latest_never_an_arbitrary_same_base_tag()
    {
        // model:old precedes model:latest in the listing. Artifact identity
        // must name what an embed request actually resolves to (:latest for a
        // tagless config) — the broad availability rule would let array order
        // stamp the wrong digest.
        var client = ClientWith(_ => TagsWithDigests(
            ("mxbai-embed-large:old", "sha256:old"),
            ("mxbai-embed-large:latest", "sha256:new")));
        (await client.GetModelArtifactDigestAsync()).ShouldBe("sha256:new");

        // An explicitly tagged config resolves only its exact tag.
        var tagged = ClientWith(_ => TagsWithDigests(
            ("qwen3-embedding:latest", "sha256:latest"),
            ("qwen3-embedding:4b", "sha256:4b")),
            new Core.Options.OllamaOptions { EmbeddingModel = "qwen3-embedding:4b" });
        (await tagged.GetModelArtifactDigestAsync()).ShouldBe("sha256:4b");

        // Only a same-base tag installed, but not :latest and not exact:
        // unobservable, never a guess.
        var strayOnly = ClientWith(_ => TagsWithDigests(("mxbai-embed-large:old", "sha256:old")));
        (await strayOnly.GetModelArtifactDigestAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Digest_is_null_when_unlisted_blank_or_unreachable()
    {
        // All three are "unobservable", which callers must treat as unknown —
        // never as drift.
        (await ClientWith(_ => TagsWithDigests(("qwen2.5vl:7b", "sha256:vision")))
            .GetModelArtifactDigestAsync()).ShouldBeNull();
        (await ClientWith(_ => TagsWithDigests(("mxbai-embed-large:latest", "")))
            .GetModelArtifactDigestAsync()).ShouldBeNull();
        (await ClientWith(_ => throw new HttpRequestException("connection refused"))
            .GetModelArtifactDigestAsync()).ShouldBeNull();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
