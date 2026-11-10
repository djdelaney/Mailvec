using Mailvec.Parse;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Parse.Tests;

public class ContentRootTests
{
    [Fact]
    public void The_host_reads_configuration_from_its_own_directory_not_the_working_directory()
    {
        // Observed on a built host, to back the source check in
        // Mailvec.Core.Tests: the setting really is what the host ends up with.
        using var app = ParseHost.Build([], url: "http://127.0.0.1:0");

        var root = app.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;

        Path.TrimEndingDirectorySeparator(root)
            .ShouldBe(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
    }
}
