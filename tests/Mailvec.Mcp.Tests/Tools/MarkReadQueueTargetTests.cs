using Mailvec.Core.Options;
using Microsoft.Extensions.Options;

namespace Mailvec.Mcp.Tests.Tools;

public class MarkReadQueueTargetTests
{
    private static MarkReadQueue Queue(string root) =>
        new(Options.Create(new McpOptions()), Options.Create(new IngestOptions { MaildirRoot = root }));

    [Fact]
    public void An_ordinary_copy_resolves_to_its_folder_and_base_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "mailvec-target-" + Guid.NewGuid().ToString("N"));

        var target = Queue(root).TargetFor(Path.Combine(root, "INBOX", "cur", "1700000000.M1.host:2,S"));

        target.ShouldNotBeNull().Dir.ShouldBe("INBOX");
        target.BaseName.ShouldBe("1700000000.M1.host");
    }

    [Theory]
    [InlineData("IN\tBOX", "1700000000.M1.host:2,S")]
    [InlineData("INBOX", "1700000000.M1\thost:2,S")]
    [InlineData("INBOX", "1700000000.M1\nread\tINBOX\tother:2,S")] // a second request line
    public void A_name_carrying_a_field_or_line_separator_is_not_a_request(string folder, string file)
    {
        // The request file is tab-separated lines; such a name would be read
        // by the sidecar as more fields, or as another request.
        var root = Path.Combine(Path.GetTempPath(), "mailvec-target-" + Guid.NewGuid().ToString("N"));

        Queue(root).TargetFor(Path.Combine(root, folder, "cur", file)).ShouldBeNull();
    }
}
