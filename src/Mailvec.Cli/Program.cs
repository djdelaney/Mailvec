using System.CommandLine;
using Mailvec.Cli.Commands;

var root = new RootCommand("Mailvec admin CLI")
{
    StatusCommand.Build(),
    SearchCommand.Build(),
    RebuildFtsCommand.Build(),
};

return await root.Parse(args).InvokeAsync();
