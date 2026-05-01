using System.CommandLine;
using Mailvec.Cli.Commands;

var root = new RootCommand("Mailvec admin CLI")
{
    StatusCommand.Build(),
    SearchCommand.Build(),
    RebuildFtsCommand.Build(),
    ReindexCommand.Build(),
    AuditEmbeddingsCommand.Build(),
    CheckpointCommand.Build(),
    EvalCommand.Build(),
    EvalAddCommand.Build(),
    EvalImportCommand.Build(),
    RebuildBodiesCommand.Build(),
};

return await root.Parse(args).InvokeAsync();
