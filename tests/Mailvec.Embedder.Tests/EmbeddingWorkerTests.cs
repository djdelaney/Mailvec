using System.Net;
using System.Net.Http.Json;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Mailvec.Embedder.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailvec.Embedder.Tests;

public class EmbeddingWorkerTests : IDisposable
{
    private readonly string _dirPath;
    private readonly string _dbPath;
    private readonly ConnectionFactory _connections;
    private readonly MessageRepository _messages;
    private readonly ChunkRepository _chunks;
    private readonly MetadataRepository _metadata;

    public EmbeddingWorkerTests()
    {
        _dirPath = Path.Combine(Path.GetTempPath(), "mailvec-emb-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dirPath);
        _dbPath = Path.Combine(_dirPath, "archive.sqlite");

        var archiveOpts = Options.Create(new ArchiveOptions { DatabasePath = _dbPath });
        _connections = new ConnectionFactory(archiveOpts);
        new SchemaMigrator(_connections, NullLogger<SchemaMigrator>.Instance).EnsureUpToDate();

        _messages = new MessageRepository(_connections);
        _chunks = new ChunkRepository(_connections);
        _metadata = new MetadataRepository(_connections);
    }

    public void Dispose()
    {
        // Scope the pool clear to THIS database (see TempDatabase) — a global
        // ClearAllPools() races with parallel test classes' in-use connections.
        using (var conn = _connections.Open())
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(conn);
        }
        try { Directory.Delete(_dirPath, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void VerifyEmbeddingModelMatchesSchema_initialises_metadata_on_empty_db()
    {
        var worker = BuildWorker(_ => Ok([HotVector(0)]));

        worker.VerifyEmbeddingModelMatchesSchema();

        _metadata.Get("embedding_model").ShouldBe("mxbai-embed-large");
        _metadata.Get("embedding_dimensions").ShouldBe("1024");
    }

    [Fact]
    public void VerifyEmbeddingModelMatchesSchema_returns_when_metadata_matches_config()
    {
        _metadata.Set("embedding_model", "mxbai-embed-large");
        _metadata.Set("embedding_dimensions", "1024");
        var worker = BuildWorker(_ => Ok([HotVector(0)]));

        Should.NotThrow(worker.VerifyEmbeddingModelMatchesSchema);
    }

    [Fact]
    public void VerifyEmbeddingModelMatchesSchema_throws_when_model_name_differs()
    {
        _metadata.Set("embedding_model", "nomic-embed-text");
        _metadata.Set("embedding_dimensions", "1024");
        var worker = BuildWorker(_ => Ok([HotVector(0)]));

        var ex = Should.Throw<InvalidOperationException>(worker.VerifyEmbeddingModelMatchesSchema);
        ex.Message.ShouldContain("nomic-embed-text");
        ex.Message.ShouldContain("mxbai-embed-large");
        ex.Message.ShouldContain("reindex");
    }

    [Fact]
    public void VerifyEmbeddingModelMatchesSchema_throws_when_dimensions_differ()
    {
        _metadata.Set("embedding_model", "mxbai-embed-large");
        _metadata.Set("embedding_dimensions", "768");
        var worker = BuildWorker(_ => Ok([HotVector(0)]));

        var ex = Should.Throw<InvalidOperationException>(worker.VerifyEmbeddingModelMatchesSchema);
        ex.Message.ShouldContain("768");
        ex.Message.ShouldContain("1024");
    }

    [Fact]
    public async Task ProcessOneBatchAsync_throws_when_model_switched_mid_run()
    {
        // `mailvec switch-model` rewrites metadata.embedding_model while the
        // embedder runs. The per-poll re-verify must stop the (old-config)
        // worker from re-embedding the re-queued archive with the old model —
        // the startup check alone would never see the switch.
        InsertMessage("msg-1@x", subject: "Hello", body: new string('a', 300));
        var worker = BuildWorker(_ => Ok([HotVector(0)]));
        worker.VerifyEmbeddingModelMatchesSchema();   // startup check passes

        _metadata.Set("embedding_model", "qwen3-embedding:4b"); // switch-model lands

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => worker.ProcessOneBatchAsync(batchSize: 16, ct: default));
        ex.Message.ShouldContain("mismatch");
        _chunks.CountForMessage(GetMessageId("msg-1@x")).ShouldBe(0);   // nothing written
    }

    [Fact]
    public async Task Poison_message_is_quarantined_and_embedding_continues()
    {
        // A message whose embed call permanently fails (non-400) is
        // re-selected head-of-line and used to fail every batch — halting ALL
        // embedding forever. After repeated batch failures the worker now
        // isolates, attributes the failure, and quarantines the message once
        // it has failed repeatedly while other messages embedded fine.
        InsertMessage("poison@x", "bad", "POISONMARKER " + new string('p', 300));
        var worker = BuildWorker(req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            if (body.Contains("POISONMARKER", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };
            return Ok([.. Enumerable.Range(0, ReadInputCount(req)).Select(i => HotVector(i))]);
        });

        // Drive the worker the way ExecuteAsync does (swallowing per-cycle
        // failures), with fresh mail arriving so isolation passes have the
        // same-cycle successes that make failures count toward quarantine.
        for (int cycle = 0; cycle < 12; cycle++)
        {
            InsertMessage($"good-{cycle}@x", "ok", new string('g', 300));
            try { await worker.ProcessNextBatchAsync(batchSize: 16, ct: default); }
            catch (InvalidOperationException) { }
            catch (HttpRequestException) { }
        }

        // Every healthy message made it through despite the poison one...
        for (int cycle = 0; cycle < 12; cycle++)
        {
            EmbeddedAt(GetMessageId($"good-{cycle}@x")).ShouldNotBeNull($"good-{cycle}@x should be embedded");
        }
        // ...the poison message is honestly unembedded (no fake stamp)...
        EmbeddedAt(GetMessageId("poison@x")).ShouldBeNull();

        // ...and once quarantined it no longer fails batches: a fresh message
        // embeds in a single clean cycle with no exception.
        InsertMessage("after@x", "ok", new string('a', 300));
        var processed = await worker.ProcessNextBatchAsync(batchSize: 16, ct: default);
        processed.ShouldBeGreaterThan(0);
        EmbeddedAt(GetMessageId("after@x")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Full_poison_window_quarantines_via_the_health_probe_and_the_queue_drains()
    {
        // Two poison messages fill the entire head-of-line window (batchSize
        // 2, ORDER BY id), so every isolation pass fails 100% of its
        // candidates. Without the embed health probe that meant zero
        // same-pass evidence -> zero strikes -> identical cycles forever, and
        // the healthy message behind the window starved permanently. The
        // probe (a one-string embed, which doesn't carry the poison marker)
        // proves Ollama healthy so strikes accrue and both quarantine out.
        InsertMessage("p1@x", "bad", "POISONMARKER " + new string('p', 300));
        InsertMessage("p2@x", "bad", "POISONMARKER " + new string('q', 300));
        InsertMessage("behind@x", "ok", new string('g', 300));
        var worker = BuildWorker(req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            if (body.Contains("POISONMARKER", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };
            return Ok([.. Enumerable.Range(0, ReadInputCount(req)).Select(i => HotVector(i))]);
        });

        for (int cycle = 0; cycle < 12; cycle++)
        {
            try { await worker.ProcessNextBatchAsync(batchSize: 2, ct: default); }
            catch (InvalidOperationException) { }
            catch (HttpRequestException) { }
        }

        // The poisons are honestly unembedded (quarantined, no fake stamp)...
        EmbeddedAt(GetMessageId("p1@x")).ShouldBeNull();
        EmbeddedAt(GetMessageId("p2@x")).ShouldBeNull();
        // ...and the message that was starving behind the all-poison window
        // made it through.
        EmbeddedAt(GetMessageId("behind@x")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Broken_ollama_never_quarantines_messages()
    {
        // When EVERY embed call fails there's no way to tell a poison message
        // from a broken Ollama — nothing may be quarantined, or an outage
        // would permanently strip messages out of the embedding queue.
        InsertMessage("m1@x", "one", new string('a', 300));
        InsertMessage("m2@x", "two", new string('b', 300));
        var healthy = false;
        var worker = BuildWorker(req => healthy
            ? Ok([.. Enumerable.Range(0, ReadInputCount(req)).Select(i => HotVector(i))])
            : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("down") });

        for (int cycle = 0; cycle < 8; cycle++)   // batch failures + all-fail isolation passes
        {
            try { await worker.ProcessNextBatchAsync(batchSize: 16, ct: default); }
            catch (InvalidOperationException) { }
            catch (HttpRequestException) { }
        }
        EmbeddedAt(GetMessageId("m1@x")).ShouldBeNull();
        EmbeddedAt(GetMessageId("m2@x")).ShouldBeNull();

        // Ollama recovers: BOTH messages embed — proof neither was quarantined.
        healthy = true;
        while (await worker.ProcessNextBatchAsync(batchSize: 16, ct: default) > 0) { }
        EmbeddedAt(GetMessageId("m1@x")).ShouldNotBeNull();
        EmbeddedAt(GetMessageId("m2@x")).ShouldNotBeNull();
    }

    [Fact]
    public async Task ProcessOneBatchAsync_returns_zero_when_no_unembedded_messages()
    {
        var called = 0;
        var worker = BuildWorker(_ => { called++; return Ok([HotVector(0)]); });

        var processed = await worker.ProcessOneBatchAsync(batchSize: 16, ct: default);

        processed.ShouldBe(0);
        called.ShouldBe(0); // never even called Ollama
    }

    [Fact]
    public async Task ProcessOneBatchAsync_embeds_long_body_only_message()
    {
        var bodyText = new string('a', 300); // long enough to be one chunk; > MinBodyCharsForVector
        InsertMessage("msg-1@x", subject: "Hello", body: bodyText);

        var captured = new List<int>();
        var worker = BuildWorker(req =>
        {
            captured.Add(ReadInputCount(req));
            return Ok([HotVector(0)]);
        });

        var processed = await worker.ProcessOneBatchAsync(batchSize: 16, ct: default);

        processed.ShouldBe(1);
        captured.ShouldBe([1]);
        _chunks.CountForMessage(GetMessageId("msg-1@x")).ShouldBe(1);
        EmbeddedAt(GetMessageId("msg-1@x")).ShouldNotBeNull();

        var chunk = ReadChunk(GetMessageId("msg-1@x"), idx: 0);
        chunk.Source.ShouldBe("body");
        chunk.AttachmentId.ShouldBeNull();
    }

    [Fact]
    public async Task ProcessOneBatchAsync_stamps_short_body_message_with_no_attachments_and_zero_chunks()
    {
        // Body shorter than MinBodyCharsForVector (100) AND no attachment text.
        // Worker must still stamp embedded_at so EnumerateUnembedded stops
        // returning the row. Without this, _processedThisRun would keep
        // incrementing each batch on the same message.
        InsertMessage("short@x", subject: "Hi", body: "tiny body");

        var called = 0;
        var worker = BuildWorker(_ => { called++; return Ok([HotVector(0)]); });

        var processed = await worker.ProcessOneBatchAsync(batchSize: 16, ct: default);

        processed.ShouldBe(1);
        called.ShouldBe(0); // no chunks → no embed call
        _chunks.CountForMessage(GetMessageId("short@x")).ShouldBe(0);
        EmbeddedAt(GetMessageId("short@x")).ShouldNotBeNull();

        // Next batch must not see this message again.
        var second = await worker.ProcessOneBatchAsync(batchSize: 16, ct: default);
        second.ShouldBe(0);
    }

    [Fact]
    public async Task ProcessOneBatchAsync_combines_body_and_attachment_chunks_with_sequential_indexes()
    {
        var bodyText = new string('b', 300);
        var messageId = InsertMessage("combo@x", subject: "Subject", body: bodyText);
        var attachmentId = InsertAttachment(messageId, partIndex: 0, fileName: "doc.pdf",
            extractedText: new string('p', 300));

        var capturedBatchSizes = new List<int>();
        var worker = BuildWorker(req =>
        {
            var n = ReadInputCount(req);
            capturedBatchSizes.Add(n);
            var vectors = Enumerable.Range(0, n).Select(i => HotVector(i)).ToArray();
            return Ok(vectors);
        });

        var processed = await worker.ProcessOneBatchAsync(batchSize: 16, ct: default);

        processed.ShouldBe(1);
        _chunks.CountForMessage(messageId).ShouldBe(2);

        var body = ReadChunk(messageId, idx: 0);
        body.Source.ShouldBe("body");
        body.AttachmentId.ShouldBeNull();

        var attachment = ReadChunk(messageId, idx: 1);
        attachment.Source.ShouldBe("attachment");
        attachment.AttachmentId.ShouldBe(attachmentId);

        capturedBatchSizes.ShouldBe([2]);
    }

    [Fact]
    public async Task ProcessOneBatchAsync_emits_attachment_chunks_when_body_is_below_threshold()
    {
        // Thin body wrapping a substantive PDF — attachment-only embedding path.
        var messageId = InsertMessage("attonly@x", subject: "See attached", body: "thanks");
        InsertAttachment(messageId, partIndex: 0, fileName: "report.pdf",
            extractedText: new string('p', 400));

        var worker = BuildWorker(req => Ok(Enumerable.Range(0, ReadInputCount(req))
            .Select(i => HotVector(i)).ToArray()));

        var processed = await worker.ProcessOneBatchAsync(batchSize: 16, ct: default);

        processed.ShouldBe(1);
        var counts = CountChunksBySource(messageId);
        counts.Body.ShouldBe(0);
        counts.Attachment.ShouldBeGreaterThan(0);
        EmbeddedAt(messageId).ShouldNotBeNull();
    }

    [Fact]
    public async Task ProcessOneBatchAsync_throws_when_vector_count_mismatches_chunk_count()
    {
        // Sanity assert in worker: the Ollama wrapper's contract says
        // vectors.Length == inputs.Count. If a future regression breaks that,
        // ChunkRepository would silently associate wrong vectors with wrong
        // chunks (vector-space corruption). The InvalidOperationException is
        // the loud-failure guard for this — keep it.
        var bodyText = new string('a', 400);
        InsertMessage("mismatch@x", subject: "S", body: bodyText);

        var worker = BuildWorker(_ => Ok([])); // returns 0 vectors for 1 input
        // OllamaClient itself will throw before returning to EmbeddingWorker
        // because parsed.Embeddings.Length (0) != inputs.Count (1).
        await Should.ThrowAsync<InvalidOperationException>(
            () => worker.ProcessOneBatchAsync(batchSize: 16, ct: default));
    }

    [Fact]
    public void BuildChunksForMessage_skips_body_below_threshold_but_keeps_attachments()
    {
        var worker = BuildWorker(_ => Ok([HotVector(0)]));

        var msg = new UnembeddedMessage(
            Id: 1,
            BodyText: "short",
            Subject: "Hi",
            AttachmentNames: "doc.pdf",
            Attachments: [new AttachmentEmbeddingPayload(
                AttachmentId: 42,
                PartIndex: 0,
                FileName: "doc.pdf",
                Text: new string('p', 300))]);

        var chunks = worker.BuildChunksForMessage(msg);

        chunks.ShouldNotBeEmpty();
        chunks.ShouldAllBe(c => c.Source == "attachment");
        chunks[0].AttachmentId.ShouldBe(42);
    }

    [Fact]
    public void BuildChunksForMessage_renumbers_chunk_indexes_across_body_and_attachments()
    {
        var worker = BuildWorker(_ => Ok([HotVector(0)]));

        // Long body forces multiple body chunks (200 token = 800 char ceiling),
        // followed by a long attachment with multiple chunks. The renumbering
        // invariant is what guarantees UNIQUE(message_id, chunk_index).
        var longBody = new string('a', 2000);
        var longAttachment = new string('p', 2000);

        var msg = new UnembeddedMessage(
            Id: 1,
            BodyText: longBody,
            Subject: "Subj",
            AttachmentNames: "doc.pdf",
            Attachments: [new AttachmentEmbeddingPayload(7, 0, "doc.pdf", longAttachment)]);

        var chunks = worker.BuildChunksForMessage(msg);

        chunks.Count.ShouldBeGreaterThan(2);
        // Sequential, starts at 0.
        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].Index.ShouldBe(i);
        }
        // Body chunks come first, then attachment chunks.
        var firstAttachment = chunks.First(c => c.Source == "attachment").Index;
        chunks.Where(c => c.Source == "body").ShouldAllBe(c => c.Index < firstAttachment);
    }

    [Fact]
    public void BuildChunksForMessage_disabled_threshold_keeps_short_body()
    {
        // MinBodyCharsForVector = 0 means "always embed body, even tiny ones".
        var worker = BuildWorker(_ => Ok([HotVector(0)]), embedderOpts: new EmbedderOptions
        {
            MinBodyCharsForVector = 0,
            ChunkSizeTokens = 200,
            ChunkOverlapTokens = 32,
        });

        var msg = new UnembeddedMessage(1, "tiny", "S", null, []);
        var chunks = worker.BuildChunksForMessage(msg);

        chunks.Count.ShouldBe(1);
        chunks[0].Source.ShouldBe("body");
    }

    [Fact]
    public async Task ExecuteAsync_drains_backlog_and_records_success_beat()
    {
        // Full run-loop path: startup → ProcessOneBatch embeds the pending
        // message → RecordBatchSuccess writes the health beat → idle Delay →
        // StopAsync cancels cleanly. The per-method ProcessOneBatch tests never
        // exercise ExecuteAsync itself.
        InsertMessage("loop@x", subject: "Hello", body: new string('a', 300));
        var worker = BuildWorker(req => Ok(Enumerable.Range(0, ReadInputCount(req))
            .Select(i => HotVector(i)).ToArray()));

        await worker.StartAsync(default);
        try
        {
            await WaitUntilAsync(
                () => _metadata.Get(EmbedderHealthKeys.LastSuccessAt) is not null,
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(default);
        }

        EmbeddedAt(GetMessageId("loop@x")).ShouldNotBeNull();
        _metadata.Get(EmbedderHealthKeys.ConsecutiveFailures).ShouldBe("0");
        _metadata.Get(EmbedderHealthKeys.LastFailureKind).ShouldBe("");
    }

    [Fact]
    public async Task ExecuteAsync_records_failure_beat_when_embedding_throws()
    {
        // A throwing provider drives the generic catch → RecordBatchFailure,
        // the signal HealthService reads to flip /health to degraded. The
        // message stays unembedded so the next poll retries it.
        InsertMessage("boom@x", subject: "Hello", body: new string('a', 300));
        var worker = BuildWorker(new FakeEmbeddingClient(
            _ => throw new HttpRequestException("ollama is down")));

        await worker.StartAsync(default);
        try
        {
            await WaitUntilAsync(
                () => _metadata.Get(EmbedderHealthKeys.ConsecutiveFailures) == "1",
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(default);
        }

        _metadata.Get(EmbedderHealthKeys.LastFailureKind).ShouldBe(nameof(HttpRequestException));
        _metadata.Get(EmbedderHealthKeys.LastFailureAt).ShouldNotBeNull();
        EmbeddedAt(GetMessageId("boom@x")).ShouldBeNull();
    }

    [Fact]
    public async Task ProcessOneBatchAsync_throws_when_client_returns_fewer_vectors_than_chunks()
    {
        // The worker's own vector-count guard, defense-in-depth behind
        // OllamaClient's identical check. A fake client that under-returns trips
        // it directly; without the guard, ChunkRepository would pair chunks with
        // the wrong vectors (silent vector-space corruption).
        InsertMessage("undercount@x", subject: "S", body: new string('a', 400));
        var worker = BuildWorker(new FakeEmbeddingClient(_ => [])); // 0 vectors for 1 chunk

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => worker.ProcessOneBatchAsync(batchSize: 16, ct: default));
        ex.Message.ShouldContain("0 vectors");
    }

    // ---------------- helpers ----------------

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        condition().ShouldBeTrue("condition was not met within the timeout");
    }

    private EmbeddingWorker BuildWorker(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        EmbedderOptions? embedderOpts = null,
        OllamaOptions? ollamaOpts = null)
    {
        var ollamaOptions = ollamaOpts ?? DefaultOllamaOptions();
        var http = new HttpClient(new StubHandler(respond))
        {
            BaseAddress = new Uri("http://localhost:11434"),
        };
        var ollamaClient = new OllamaClient(http, Options.Create(ollamaOptions), NullLogger<OllamaClient>.Instance);
        return Assemble(ollamaClient, embedderOpts, ollamaOptions);
    }

    private EmbeddingWorker BuildWorker(
        IEmbeddingClient client,
        EmbedderOptions? embedderOpts = null,
        OllamaOptions? ollamaOpts = null)
        => Assemble(client, embedderOpts, ollamaOpts ?? DefaultOllamaOptions());

    private static OllamaOptions DefaultOllamaOptions() => new()
    {
        EmbeddingModel = "mxbai-embed-large",
        EmbeddingDimensions = 1024,
        MaxBatchSize = 16,
    };

    private EmbeddingWorker Assemble(IEmbeddingClient client, EmbedderOptions? embedderOpts, OllamaOptions ollamaOptions)
    {
        var ollamaOptionsW = Options.Create(ollamaOptions);
        var embedderOptionsW = Options.Create(embedderOpts ?? new EmbedderOptions
        {
            PollIntervalSeconds = 60,
            ChunkSizeTokens = 200,
            ChunkOverlapTokens = 32,
            MinBodyCharsForVector = 100,
        });

        var chunker = new ChunkingService(embedderOptionsW);
        var migrator = new SchemaMigrator(_connections, NullLogger<SchemaMigrator>.Instance);

        return new EmbeddingWorker(
            migrator, _metadata, _messages, _chunks, chunker, client,
            embedderOptionsW, ollamaOptionsW, NullLogger<EmbeddingWorker>.Instance);
    }

    private static HttpResponseMessage Ok(float[][] embeddings) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { model = "test", embeddings }),
        };

    private static float[] HotVector(int hotIndex, int dim = 1024)
    {
        var v = new float[dim];
        v[hotIndex % dim] = 1f;
        return v;
    }

    private static int ReadInputCount(HttpRequestMessage req)
    {
        var body = req.Content!.ReadAsStringAsync().Result;
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("input").GetArrayLength();
    }

    private long InsertMessage(string messageId, string subject, string body)
    {
        using var conn = _connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (message_id, maildir_path, maildir_filename, folder, subject, body_text, indexed_at)
            VALUES ($mid, 'INBOX/cur', 'fake', 'INBOX', $subj, $body, $now)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);
        cmd.Parameters.AddWithValue("$subj", subject);
        cmd.Parameters.AddWithValue("$body", body);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(cmd.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private long InsertAttachment(long messageId, int partIndex, string fileName, string extractedText)
    {
        using var conn = _connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO attachments (message_id, part_index, filename, extracted_text, extraction_status, extracted_at)
            VALUES ($mid, $idx, $fn, $txt, 'done', $now)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);
        cmd.Parameters.AddWithValue("$idx", partIndex);
        cmd.Parameters.AddWithValue("$fn", fileName);
        cmd.Parameters.AddWithValue("$txt", extractedText);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(cmd.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private long GetMessageId(string messageIdHeader)
    {
        using var conn = _connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM messages WHERE message_id = $mid";
        cmd.Parameters.AddWithValue("$mid", messageIdHeader);
        return Convert.ToInt64(cmd.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private string? EmbeddedAt(long id)
    {
        using var conn = _connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT embedded_at FROM messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    private (string Source, long? AttachmentId) ReadChunk(long messageId, int idx)
    {
        using var conn = _connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source, attachment_id FROM chunks WHERE message_id = $mid AND chunk_index = $idx";
        cmd.Parameters.AddWithValue("$mid", messageId);
        cmd.Parameters.AddWithValue("$idx", idx);
        using var reader = cmd.ExecuteReader();
        reader.Read().ShouldBeTrue();
        var source = reader.GetString(0);
        long? aid = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        return (source, aid);
    }

    private (int Body, int Attachment) CountChunksBySource(long messageId)
    {
        using var conn = _connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              SUM(CASE WHEN source = 'body'       THEN 1 ELSE 0 END),
              SUM(CASE WHEN source = 'attachment' THEN 1 ELSE 0 END)
            FROM chunks WHERE message_id = $mid
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var body = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        var attachment = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        return (body, attachment);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    /// <summary>
    /// Drives EmbeddingWorker through the IEmbeddingClient seam so tests can
    /// force failures or wrong-shaped responses that the real OllamaClient (and
    /// its own validation) wouldn't let through.
    /// </summary>
    private sealed class FakeEmbeddingClient(Func<IReadOnlyList<string>, float[][]> embed) : IEmbeddingClient
    {
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            Task.FromResult(embed(inputs));

        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool?> IsModelAvailableAsync(CancellationToken ct = default) => Task.FromResult<bool?>(true);
    }
}
