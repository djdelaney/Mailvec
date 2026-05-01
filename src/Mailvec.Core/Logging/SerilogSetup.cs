using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Mailvec.Core.Logging;

/// <summary>
/// Single source of truth for Serilog wiring across Mailvec.Indexer,
/// Mailvec.Embedder, and Mailvec.Mcp. Replaces the custom bash log rotator —
/// Serilog's File sink owns the file handle, so rotation is atomic and doesn't
/// have to dance around launchd's inherited stdout/stderr fds.
///
/// Output:
///   - Rolling file at <logDir>/mailvec-&lt;service&gt;-&lt;date&gt;.log,
///     daily rolling, also rolls if a single day exceeds 10 MB,
///     14 most recent files retained.
///   - Console sink (stdout) by default — useful for `dotnet run` during dev.
///     Skipped when MAILVEC_LAUNCHD=1 is set so production doesn't double-write
///     into the launchd-captured StandardOutPath.
///     Forced to stderr when stdioMode=true (the MCP stdio transport reserves
///     stdout for JSON-RPC framing — a single byte on stdout corrupts the protocol).
///
/// Log dir resolves from MAILVEC_LOG_DIR env var, then ~/Library/Logs/Mailvec/.
///
/// Takes the constituent builder pieces rather than HostApplicationBuilder /
/// WebApplicationBuilder so Mailvec.Core doesn't have to reference
/// Microsoft.Extensions.Hosting or AspNetCore.
/// </summary>
public static class SerilogSetup
{
    public static void Configure(
        IServiceCollection services,
        IConfiguration configuration,
        ILoggingBuilder logging,
        string serviceName,
        bool stdioMode = false)
    {
        var logDir = ResolveLogDir();
        Directory.CreateDirectory(logDir);

        var config = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.WithProperty("service", serviceName)
            .WriteTo.File(
                path: Path.Combine(logDir, $"mailvec-{serviceName}-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14,
                shared: false,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

        var underLaunchd = string.Equals(
            Environment.GetEnvironmentVariable("MAILVEC_LAUNCHD"),
            "1",
            StringComparison.Ordinal);

        if (stdioMode)
        {
            // MCP stdio: stdout carries JSON-RPC frames. Route every level to stderr.
            config = config.WriteTo.Console(
                standardErrorFromLevel: LogEventLevel.Verbose);
        }
        else if (!underLaunchd)
        {
            // Dev / `dotnet run`: emit to stdout for terminal visibility.
            config = config.WriteTo.Console();
        }
        // else: under launchd in production — skip Console sink so launchd's
        // captured StandardOutPath doesn't duplicate the rolling file.

        var logger = config.CreateLogger();
        logging.ClearProviders();
        services.AddSerilog(logger, dispose: true);
    }

    private static string ResolveLogDir()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MAILVEC_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return PathExpansion.Expand(fromEnv);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "Logs", "Mailvec");
    }
}
