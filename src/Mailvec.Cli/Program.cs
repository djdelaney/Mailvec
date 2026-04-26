using System.CommandLine;

var root = new RootCommand("Mailvec admin CLI");

// TODO Phase 1: archive status, archive search "<query>"
// TODO Phase 1: archive rebuild-fts
// TODO Phase 2: archive reindex [--all|--folder=...]

return await root.Parse(args).InvokeAsync();
