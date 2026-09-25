// Synthetic Maildir for running Mailvec by hand where there is no real mail.
// Usage and layout: docs/contributing/dev-corpus.md.
//
//   dotnet run --project tools/Mailvec.DevCorpus -- <dir> [--hazards] [--filler N]

using Mailvec.DevCorpus;

string? target = null;
var hazards = false;
var filler = Corpus.DefaultFiller;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--hazards":
            hazards = true;
            break;
        case "--filler" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n >= 0:
            filler = n;
            i++;
            break;
        case "-h" or "--help":
            return Usage(0);
        case var a when a.StartsWith('-'):
            Console.Error.WriteLine($"unknown or malformed option: {a}");
            return Usage(2);
        default:
            if (target is not null) return Usage(2);
            target = args[i];
            break;
    }
}
if (target is null) return Usage(2);

try
{
    var root = CorpusWriter.Write(target, new CorpusOptions(hazards, filler));
    var corpus = CorpusWriter.Build(new CorpusOptions(hazards, filler));
    Console.WriteLine($"Wrote a synthetic corpus to {root}");
    Console.WriteLine($"  {corpus.Scenarios.Count} scenarios, {corpus.Filler.Count} filler messages, "
                      + $"{corpus.Hazards.Count} hazards, {corpus.Eval.Count} eval queries");
    Console.WriteLine($"  Next: . '{Path.Combine(root, "env.sh")}'   (the file lists what to run)");
    return 0;
}
catch (RefusedException ex)
{
    Console.Error.WriteLine($"refusing: {ex.Message}");
    return 1;
}

static int Usage(int code)
{
    (code == 0 ? Console.Out : Console.Error).WriteLine(
        "usage: Mailvec.DevCorpus <dir> [--hazards] [--filler N]\n"
        + "  <dir>       must not exist, or be empty; never inside a real Maildir\n"
        + $"  --filler N  background messages (default {Corpus.DefaultFiller})\n"
        + "  --hazards   add the cases Mailvec refuses or degrades on by design");
    return code;
}
