using Mailvec.Cli.Commands;
using Mailvec.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Tests;

public class SwitchModelCommandTests
{
    [Fact]
    public void NoOp_when_database_already_matches_target()
    {
        using var tsp = new TestServiceProvider();
        var @out = new StringWriter();

        // Default OllamaOptions (mxbai-embed-large/1024) match the fresh schema's seed.
        var exit = SwitchModelCommand.Execute(tsp.Services, model: null, dims: null, yes: false, @out, () => throw new InvalidOperationException("should not prompt"));

        exit.ShouldBe(0);
        @out.ToString().ShouldContain("Nothing to do");
        ReadMetadata(tsp.Connections, "embedding_model").ShouldBe("mxbai-embed-large");
    }

    [Fact]
    public void Aborts_and_leaves_database_untouched_when_prompt_declined()
    {
        using var tsp = new TestServiceProvider();
        var @out = new StringWriter();

        var exit = SwitchModelCommand.Execute(tsp.Services, "qwen3-embedding:4b", 2560, yes: false, @out, () => "n");

        exit.ShouldBe(1);
        @out.ToString().ShouldContain("Aborted");
        ReadMetadata(tsp.Connections, "embedding_model").ShouldBe("mxbai-embed-large");
        ReadMetadata(tsp.Connections, "embedding_dimensions").ShouldBe("1024");
    }

    [Fact]
    public void Switches_model_and_requeues_messages_with_yes()
    {
        using var tsp = new TestServiceProvider();
        SeedMessage(tsp.Connections);
        var @out = new StringWriter();

        var exit = SwitchModelCommand.Execute(tsp.Services, "qwen3-embedding:4b", 2560, yes: true, @out, () => throw new InvalidOperationException("should not prompt"));

        exit.ShouldBe(0);
        var text = @out.ToString();
        text.ShouldContain("qwen3-embedding:4b (2560d)");
        text.ShouldContain("ollama pull qwen3-embedding:4b");
        ReadMetadata(tsp.Connections, "embedding_model").ShouldBe("qwen3-embedding:4b");
        ReadMetadata(tsp.Connections, "embedding_dimensions").ShouldBe("2560");
        CountEmbeddedNull(tsp.Connections).ShouldBe(1);
    }

    [Fact]
    public void Interactive_confirmation_proceeds_on_y()
    {
        using var tsp = new TestServiceProvider();
        var @out = new StringWriter();

        var exit = SwitchModelCommand.Execute(tsp.Services, "qwen3-embedding:0.6b", 1024, yes: false, @out, () => "y");

        exit.ShouldBe(0);
        ReadMetadata(tsp.Connections, "embedding_model").ShouldBe("qwen3-embedding:0.6b");
        ReadMetadata(tsp.Connections, "embedding_dimensions").ShouldBe("1024");
    }

    private static void SeedMessage(ConnectionFactory connections)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (message_id, maildir_path, maildir_filename, folder, subject, indexed_at, embedded_at)
            VALUES ('seed@x', 'INBOX/cur', 'f1', 'INBOX', 'seeded', '2025-01-01T00:00:00.0000000+00:00', '2025-01-01T00:00:00.0000000+00:00')
            """;
        cmd.ExecuteNonQuery();
    }

    private static string? ReadMetadata(ConnectionFactory connections, string key)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM metadata WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static long CountEmbeddedNull(ConnectionFactory connections)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE embedded_at IS NULL";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
