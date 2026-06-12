using System.CommandLine;
using Mailvec.Cli.Commands;

var root = new RootCommand("Mailvec admin CLI")
{
    StatusCommand.Build(),
    DoctorCommand.Build(),
    SearchCommand.Build(),
    GetCommand.Build(),
    RebuildFtsCommand.Build(),
    ReindexCommand.Build(),
    SwitchModelCommand.Build(),
    AuditEmbeddingsCommand.Build(),
    CheckpointCommand.Build(),
    PurgeDeletedCommand.Build(),
    RepairCommand.Build(),
    ExtractAttachmentsCommand.Build(),
    EvalCommand.Build(),
    EvalAddCommand.Build(),
    EvalImportCommand.Build(),
    RebuildBodiesCommand.Build(),
};

return await root.Parse(args).InvokeAsync();
