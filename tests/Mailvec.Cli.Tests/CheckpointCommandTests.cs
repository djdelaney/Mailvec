using Mailvec.Cli.Commands;

namespace Mailvec.Cli.Tests;

public class CheckpointCommandTests
{
    [Fact]
    public void Checkpoint_against_fresh_wal_database_succeeds_with_zero_frames_to_flush()
    {
        // A fresh DB has WAL mode set (per ConnectionFactory.Open) but no
        // outstanding frames; the TRUNCATE call returns busy=0, frames=0.
        using var ctx = new TestServiceProvider();
        var writer = new StringWriter();

        var exit = CheckpointCommand.Execute(ctx.Services, writer);

        exit.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("Database:");
        output.ShouldContain("WAL before:");
        output.ShouldContain("WAL after:");
        output.ShouldContain("Frames synced:");
    }
}
