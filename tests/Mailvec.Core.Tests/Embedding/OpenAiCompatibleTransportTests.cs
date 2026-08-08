using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mailvec.Core.Embedding;

namespace Mailvec.Core.Tests.Embedding;

/// <summary>
/// The hosted transport against stub HTTP fixtures — CI never needs a live
/// key. Request rules, index-ordered reassembly, classification, and the
/// no-upstream-body sanitization rule, per the proposal's transport test plan.
/// </summary>
public class OpenAiCompatibleTransportTests
{
    private static ResolvedEmbeddingProfile FireworksLike(
        bool sendModel = true, bool sendDims = true, string? encoding = "float") => new(
        Name: "fireworks-qwen",
        Protocol: "openai-compatible",
        ProviderId: "fireworks",
        Endpoint: "https://api.example.test/inference/v1/embeddings",
        WireModel: "accounts/fireworks/models/qwen3-embedding-8b",
        OutputDimensions: 4,
        SpaceId: "fireworks:qwen3-embedding-8b:4:test",
        QueryPrefix: "", QuerySuffix: "", DocumentPrefix: "", DocumentSuffix: "",
        MaxBatchSize: 16, RequestTimeoutSeconds: 60,
        SendWireModel: sendModel, SendDimensions: sendDims, EncodingFormat: encoding);

    private static OpenAiCompatibleTransport Transport(
        Func<HttpRequestMessage, HttpResponseMessage> respond, ResolvedEmbeddingProfile? profile = null)
    {
        var p = profile ?? FireworksLike();
        var http = new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri(p.Endpoint) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-key");
        return new OpenAiCompatibleTransport(http, p);
    }

    private static HttpResponseMessage Ok(params (int Index, float[] Vec)[] items) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            @object = "list",
            model = "accounts/fireworks/models/qwen3-embedding-8b",
            data = items.Select(i => new { @object = "embedding", index = i.Index, embedding = i.Vec }).ToArray(),
            usage = new { prompt_tokens = 11, total_tokens = 11 },
        }),
    };

    [Fact]
    public async Task Sends_bearer_model_inputs_dimensions_and_encoding_but_never_ollama_fields()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var transport = Transport(req =>
        {
            captured = req;
            body = req.Content!.ReadAsStringAsync().Result;
            return Ok((0, [1f, 0f, 0f, 0f]));
        });

        await transport.EmbedAsync(["hello"]);

        captured!.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        captured.RequestUri!.AbsoluteUri.ShouldBe("https://api.example.test/inference/v1/embeddings");
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("model").GetString().ShouldBe("accounts/fireworks/models/qwen3-embedding-8b");
        doc.RootElement.GetProperty("dimensions").GetInt32().ShouldBe(4);
        doc.RootElement.GetProperty("encoding_format").GetString().ShouldBe("float");
        doc.RootElement.GetProperty("input").GetArrayLength().ShouldBe(1);
        doc.RootElement.TryGetProperty("keep_alive", out _).ShouldBeFalse();
        doc.RootElement.TryGetProperty("truncate", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Omit_policies_serialize_as_declared_while_width_stays_validated_upstream()
    {
        string? body = null;
        var transport = Transport(req =>
        {
            body = req.Content!.ReadAsStringAsync().Result;
            return Ok((0, [1f, 0f, 0f, 0f]));
        }, FireworksLike(sendModel: false, sendDims: false, encoding: null));

        await transport.EmbedAsync(["hello"]);

        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.TryGetProperty("model", out _).ShouldBeFalse();
        doc.RootElement.TryGetProperty("dimensions", out _).ShouldBeFalse();
        doc.RootElement.TryGetProperty("encoding_format", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Reorders_by_index_and_returns_raw_vectors()
    {
        // Response order is NOT trusted; the vectors are returned raw
        // (unnormalized — EmbeddingService owns the mathematical contract).
        var transport = Transport(_ => Ok((1, [0f, 9f, 0f, 0f]), (0, [9f, 0f, 0f, 0f])));

        var result = await transport.EmbedAsync(["a", "b"]);

        result[0].ShouldBe(new[] { 9f, 0f, 0f, 0f });
        result[1].ShouldBe(new[] { 0f, 9f, 0f, 0f });
    }

    [Fact]
    public async Task Rejects_duplicate_missing_and_out_of_range_indexes_and_wrong_count()
    {
        foreach (var response in new[]
        {
            Ok((0, new[] { 1f, 0f, 0f, 0f }), (0, new[] { 0f, 1f, 0f, 0f })),   // duplicate
            Ok((0, new[] { 1f, 0f, 0f, 0f }), (5, new[] { 0f, 1f, 0f, 0f })),   // out of range
            Ok((0, new[] { 1f, 0f, 0f, 0f })),                                   // wrong count
        })
        {
            var transport = Transport(_ => response);
            var ex = await Should.ThrowAsync<EmbeddingException>(() => transport.EmbedAsync(["a", "b"]));
            ex.Kind.ShouldBe(EmbeddingFailureKind.InvalidResponse);
        }
    }

    [Fact]
    public async Task Classifies_status_codes_per_the_taxonomy_without_leaking_the_body()
    {
        var secret = "the-upstream-echoed-mail-content";
        foreach (var (status, expected) in new[]
        {
            (HttpStatusCode.Unauthorized, EmbeddingFailureKind.AuthOrConfig),
            (HttpStatusCode.Forbidden, EmbeddingFailureKind.AuthOrConfig),
            (HttpStatusCode.NotFound, EmbeddingFailureKind.ModelUnavailable),
            (HttpStatusCode.TooManyRequests, EmbeddingFailureKind.Backpressure),
            (HttpStatusCode.ServiceUnavailable, EmbeddingFailureKind.Backpressure),
            (HttpStatusCode.InternalServerError, EmbeddingFailureKind.Transient),
            (HttpStatusCode.BadRequest, EmbeddingFailureKind.AuthOrConfig),      // plain 400: no blind retry
        })
        {
            var transport = Transport(_ => new HttpResponseMessage(status)
            {
                Content = new StringContent($"{{\"error\":\"{secret}\"}}"),
            });
            var ex = await Should.ThrowAsync<EmbeddingException>(() => transport.EmbedAsync(["x"]));
            ex.Kind.ShouldBe(expected, status.ToString());
            ex.Message.ShouldNotContain(secret);
        }
    }

    [Fact]
    public async Task A_positively_identified_length_400_classifies_InputTooLong()
    {
        var transport = Transport(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"input exceeds the maximum context length\"}"),
        });
        var ex = await Should.ThrowAsync<EmbeddingException>(() => transport.EmbedAsync(["x"]));
        ex.Kind.ShouldBe(EmbeddingFailureKind.InputTooLong);
    }

    [Fact]
    public async Task Network_failure_is_transient_and_malformed_json_is_invalid_response()
    {
        var down = Transport(_ => throw new HttpRequestException("connection refused"));
        (await Should.ThrowAsync<EmbeddingException>(() => down.EmbedAsync(["x"])))
            .Kind.ShouldBe(EmbeddingFailureKind.Transient);

        var garbled = Transport(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json at all"),
        });
        (await Should.ThrowAsync<EmbeddingException>(() => garbled.EmbedAsync(["x"])))
            .Kind.ShouldBe(EmbeddingFailureKind.InvalidResponse);
    }

    [Fact]
    public async Task Empty_list_short_circuits_and_empty_strings_are_refused_before_http()
    {
        var called = false;
        var transport = Transport(_ => { called = true; return Ok((0, [1f, 0f, 0f, 0f])); });

        (await transport.EmbedAsync([])).ShouldBeEmpty();
        await Should.ThrowAsync<EmbeddingException>(() => transport.EmbedAsync([""]));
        called.ShouldBeFalse();
    }

    [Fact]
    public async Task Model_catalog_is_not_a_requirement_for_hosted_profiles()
    {
        // Null = unknown: the probe must never refine a hosted failure into a
        // missing-model claim, and the digest default is likewise null
        // (unobservable weights — the sentinel check is their integrity leg).
        var transport = Transport(_ => Ok((0, [1f, 0f, 0f, 0f])));
        (await transport.IsModelAvailableAsync()).ShouldBeNull();
        (await ((IEmbeddingTransport)transport).GetModelArtifactDigestAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Telemetry_captures_usage_model_request_id_and_rate_limits_when_present()
    {
        EmbeddingTelemetry? seen = null;
        var observer = new CapturingObserver(t => seen = t);
        var p = FireworksLike();
        var response = Ok((0, [1f, 0f, 0f, 0f]));
        response.Headers.Add("x-request-id", "req-123");
        response.Headers.Add("x-ratelimit-remaining-requests", "5999");
        response.Headers.Add("x-ratelimit-remaining-tokens", "899000");
        var http = new HttpClient(new StubHandler(_ => response)) { BaseAddress = new Uri(p.Endpoint) };

        await new OpenAiCompatibleTransport(http, p, observer).EmbedAsync(["hello"]);

        seen.ShouldNotBeNull();
        seen!.PromptTokens.ShouldBe(11);
        seen.ResponseModel.ShouldBe("accounts/fireworks/models/qwen3-embedding-8b");
        seen.RequestId.ShouldBe("req-123");
        seen.RateLimitRemainingRequests.ShouldBe(5999);
        seen.RateLimitRemainingTokens.ShouldBe(899000);
    }

    [Fact]
    public async Task Absent_telemetry_is_normal_and_a_throwing_observer_never_fails_the_embed()
    {
        // Missing telemetry is not an invalid response (proposal rule), and
        // observation must never take down the embed it observed.
        EmbeddingTelemetry? seen = null;
        var p = FireworksLike();
        var bare = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { data = new[] { new { index = 0, embedding = new[] { 1f, 0f, 0f, 0f } } } }),
        };
        var http = new HttpClient(new StubHandler(_ => bare)) { BaseAddress = new Uri(p.Endpoint) };
        await new OpenAiCompatibleTransport(http, p, new CapturingObserver(t => seen = t)).EmbedAsync(["x"]);
        seen.ShouldNotBeNull();
        seen!.PromptTokens.ShouldBeNull();
        seen.ResponseModel.ShouldBeNull();
        seen.RequestId.ShouldBeNull();

        var http2 = new HttpClient(new StubHandler(_ => Ok((0, [1f, 0f, 0f, 0f])))) { BaseAddress = new Uri(p.Endpoint) };
        var throwing = new CapturingObserver(_ => throw new InvalidOperationException("observer bug"));
        (await new OpenAiCompatibleTransport(http2, p, throwing).EmbedAsync(["x"])).Length.ShouldBe(1);
    }

    private sealed class CapturingObserver(Action<EmbeddingTelemetry> onSeen) : IEmbeddingTelemetryObserver
    {
        public void OnEmbeddingResponse(EmbeddingTelemetry telemetry) => onSeen(telemetry);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
