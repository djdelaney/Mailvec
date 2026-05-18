using Mailvec.Core.Data;
using Mailvec.Core.Health;
using Mailvec.Core.Options;
using Mailvec.Core.Tests.Data;
using Mailvec.Core.Tray;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mailvec.Core.Tests.Tray;

/// <summary>
/// Tests for <see cref="TraySystemService"/>. The service is mostly a fan-out
/// of pure-formatting helpers + a few file/launchd reads. We test the helpers
/// directly (internal visibility) and the end-to-end BuildAsync path against
/// a temp DB with no launchd agents present (the launchctl call returns
/// "unloaded" rows, which the service tolerates).
/// </summary>
public class TraySystemServiceTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(1024 * 1024, "1.0 MB")]
    [InlineData(15L * 1024 * 1024, "15.0 MB")]
    [InlineData(2L * 1024 * 1024 * 1024, "2.0 GB")]
    public void ByteString_formats_powers_of_two(long bytes, string expected)
    {
        TraySystemService.ByteString(bytes).ShouldBe(expected);
    }

    [Theory]
    [InlineData(60, "1 minute")]
    [InlineData(300, "5 minutes")]
    [InlineData(600, "10 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(7200, "2 hours")]
    [InlineData(45, "45 seconds")]    // non-multiples
    [InlineData(0, "unknown")]
    [InlineData(-1, "unknown")]
    public void FormatSchedule_renders_human_strings(int seconds, string expected)
    {
        TraySystemService.FormatSchedule(seconds).ShouldBe(expected);
    }

    [Fact]
    public void ParseMbsyncrc_extracts_first_Host_and_User_directives()
    {
        var path = Path.Combine(Path.GetTempPath(), "mailvec-mbsyncrc-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, """
                # mbsync configuration
                IMAPAccount fastmail
                Host imap.fastmail.com
                User dan@example.com
                AuthMechs LOGIN

                IMAPAccount second
                Host imap.other.com
                User other@example.com
                """);

            var (host, user) = TraySystemService.ParseMbsyncrc(path);

            host.ShouldBe("imap.fastmail.com");
            user.ShouldBe("dan@example.com");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseMbsyncrc_returns_nulls_when_file_missing()
    {
        var (host, user) = TraySystemService.ParseMbsyncrc("/tmp/does-not-exist-mailvec-" + Guid.NewGuid().ToString("N"));
        host.ShouldBeNull();
        user.ShouldBeNull();
    }

    [Fact]
    public void ParseMbsyncrc_handles_tabs_and_inline_comments_gracefully()
    {
        var path = Path.Combine(Path.GetTempPath(), "mailvec-mbsyncrc-" + Guid.NewGuid().ToString("N"));
        try
        {
            // mbsync syntax: directive<space|tab>value. Comments are full-line
            // only (lines that start with `#`).
            File.WriteAllText(path, "Host\timap.fastmail.com\n# Host imap.evil.com\nUser dan\n");

            var (host, user) = TraySystemService.ParseMbsyncrc(path);
            host.ShouldBe("imap.fastmail.com");
            user.ShouldBe("dan");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExtractJsonString_pulls_value_out_of_manifest_style_json()
    {
        var json = """{ "name": "mailvec", "version": "0.1.16" }""";
        TraySystemService.ExtractJsonString(json, "version").ShouldBe("0.1.16");
        TraySystemService.ExtractJsonString(json, "name").ShouldBe("mailvec");
    }

    [Fact]
    public void ExtractJsonString_returns_null_when_key_missing()
    {
        TraySystemService.ExtractJsonString("""{"name": "mailvec"}""", "version").ShouldBeNull();
    }

    [Fact]
    public void Relative_reports_running_idle_or_never()
    {
        TraySystemService.Relative(
            new LaunchdServiceInfo("com.mailvec.mbsync", true, "running", 1234, 0, 12)).ShouldBe("now");
        TraySystemService.Relative(
            new LaunchdServiceInfo("com.mailvec.mbsync", true, "not running", null, 0, 0)).ShouldBe("never");
        TraySystemService.Relative(
            new LaunchdServiceInfo("com.mailvec.mbsync", true, "not running", null, 0, 7)).ShouldBe("after 7 runs");
    }

    [Fact]
    public void Detail_describes_state_with_exit_code_and_run_count()
    {
        TraySystemService.Detail(
            new LaunchdServiceInfo("com.mailvec.mbsync", true, "running", 12, 0, 5))
            .ShouldBe("mbsync currently syncing");
        TraySystemService.Detail(
            new LaunchdServiceInfo("com.mailvec.mbsync", true, "not running", null, 1, 5))
            .ShouldContain("last exit 1");
        TraySystemService.Detail(
            new LaunchdServiceInfo("com.mailvec.mbsync", true, "not running", null, null, 5))
            .ShouldContain("no exit recorded");
    }

    [Fact]
    public async Task BuildAsync_returns_a_system_snapshot_against_empty_db()
    {
        using var db = new TempDatabase();
        var metadata = new MetadataRepository(db.Connections);
        metadata.Set("schema_version", "1");
        metadata.Set("embedding_model", "mxbai-embed-large");
        metadata.Set("embedding_dimensions", "1024");

        // Stub Ollama so PingAsync returns failure quickly without touching the network.
        var http = new HttpClient(new UnreachableHandler())
        {
            BaseAddress = new Uri("http://localhost:11434"),
        };
        var ollamaOpts = Microsoft.Extensions.Options.Options.Create(new OllamaOptions
        {
            BaseUrl = "http://localhost:11434",
            EmbeddingDimensions = 1024,
            EmbeddingModel = "mxbai-embed-large",
        });
        var ollama = new Mailvec.Core.Ollama.OllamaClient(http, ollamaOpts, NullLogger<Mailvec.Core.Ollama.OllamaClient>.Instance);
        var archiveOpts = Microsoft.Extensions.Options.Options.Create(new ArchiveOptions { DatabasePath = db.DatabasePath });
        var ingestOpts = Microsoft.Extensions.Options.Options.Create(new IngestOptions { MaildirRoot = "/tmp/mail" });
        var mcpOpts = Microsoft.Extensions.Options.Options.Create(new McpOptions
        {
            BindAddress = "127.0.0.1",
            Port = 3333,
            AttachmentDownloadDir = "~/Downloads/mailvec",
        });
        var health = new HealthService(db.Connections, metadata, ollama, archiveOpts, ollamaOpts);
        var launchd = new LaunchdInspector(NullLogger<LaunchdInspector>.Instance);

        var service = new TraySystemService(
            health, launchd, db.Connections, metadata, ollama,
            archiveOpts, ingestOpts, ollamaOpts, mcpOpts);

        var snapshot = await service.BuildAsync();

        snapshot.SchemaVersion.ShouldBe("1");
        snapshot.EmbeddingModel.ShouldBe("mxbai-embed-large");
        snapshot.ModelDimensions.ShouldBe(1024);
        snapshot.SchemaModelMatches.ShouldBeTrue();
        snapshot.OllamaEndpoint.ShouldBe("http://localhost:11434");
        snapshot.OllamaReachable.ShouldBeFalse();
        snapshot.McpBindAddress.ShouldBe("127.0.0.1");
        snapshot.McpPort.ShouldBe(3333);
        snapshot.DbPath.ShouldBe(db.DatabasePath);
        snapshot.DbSize.ShouldEndWith("B");        // KB / MB / B all end with B
        snapshot.CoverageTotal.ShouldBe(0);
        snapshot.CoverageDone.ShouldBe(0);
        snapshot.SoftDeletedCount.ShouldBe(0);
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"));
    }
}
