using System.Net;
using System.Net.Http.Json;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailvec.Mcp.Tests;

internal static class Helpers
{
    public static ParsedMessage Sample(
        string id,
        string subject = "subj",
        string body = "ramen body text",
        string? from = "alice@example.com",
        string? fromName = null,
        string? threadId = null,
        DateTimeOffset? dateSent = null,
        IReadOnlyList<ParsedAttachment>? attachments = null) => new(
        MessageId: id,
        ThreadId: threadId ?? id,
        Subject: subject,
        FromAddress: from,
        FromName: fromName,
        ToAddresses: [],
        CcAddresses: [],
        DateSent: dateSent ?? DateTimeOffset.UtcNow,
        BodyText: body,
        BodyHtml: null,
        RawHeaders: $"Message-ID: <{id}>\r\n",
        SizeBytes: 100,
        ContentHash: $"hash-{id}",
        Attachments: attachments ?? []);

    public static IOptions<McpOptions> Mcp(McpOptions? opts = null) =>
        Options.Create(opts ?? new McpOptions());

    public static IOptions<FastmailOptions> Fastmail(FastmailOptions? opts = null) =>
        Options.Create(opts ?? new FastmailOptions());

    public static ToolCallLogger NoopLogger() =>
        new(NullLogger<ToolCallLogger>.Instance, Mcp());

    /// <summary>
    /// Real OllamaClient backed by a stub HttpMessageHandler that returns one
    /// 1024-dim one-hot vector per input. Lets tests exercise semantic/hybrid
    /// search modes without standing up a real Ollama process.
    /// </summary>
    public static OllamaClient StubOllama(int hotIndex = 0, int dim = 1024)
    {
        HttpResponseMessage Respond(HttpRequestMessage _)
        {
            // Return embeddings of the requested input count. Body shape matches
            // /api/embed: { "embeddings": [ [...], [...] ] }
            // We don't know the input count without parsing the request body,
            // so return a generator: parse the request to count inputs.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    model = "test",
                    embeddings = new[] { OneHot(hotIndex, dim) },
                }),
            };
        }

        var http = new HttpClient(new StubHandler(Respond)) { BaseAddress = new Uri("http://localhost:11434") };
        return new OllamaClient(
            http,
            Options.Create(new OllamaOptions { EmbeddingDimensions = dim }),
            NullLogger<OllamaClient>.Instance);
    }

    public static float[] OneHot(int hotIndex, int dim = 1024)
    {
        var v = new float[dim];
        v[hotIndex] = 1f;
        return v;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
