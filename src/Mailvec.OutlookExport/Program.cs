using System.Runtime.Versioning;

namespace Mailvec.OutlookExport;

public static class Program
{
    public static int Main(string[] args)
    {
        ExportOptions options;
        try
        {
            options = ExportOptions.Parse(args);
        }
        catch (HelpRequestedException)
        {
            Console.WriteLine(ExportOptions.Usage);
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(ExportOptions.Usage);
            return 1;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(
                "This tool drives classic Outlook over COM and only runs on Windows. " +
                "Publish with: dotnet publish src/Mailvec.OutlookExport -c Release -r win-x64 " +
                "--self-contained -p:PublishSingleFile=true");
            return 2;
        }

        return RunWindows(options);
    }

    [SupportedOSPlatform("windows")]
    private static int RunWindows(ExportOptions options)
    {
        try
        {
            Console.WriteLine($"Exporting to {options.OutDir}" + (options.DryRun ? " (dry run)" : ""));
            var totals = new OutlookExporter(options).Run();
            Console.WriteLine(
                $"Done: exported={totals.Exported} skipped={totals.Skipped} " +
                $"failed={totals.Failed} attachmentsDropped={totals.AttachmentsDropped}");
            return totals.Failed > 0 ? 3 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Export aborted: {ex.Message}");
            Console.Error.WriteLine(
                "Hints: is classic Outlook installed and running? If a corporate policy " +
                "disables programmatic access, this will fail on the first COM call.");
            return 1;
        }
    }
}
