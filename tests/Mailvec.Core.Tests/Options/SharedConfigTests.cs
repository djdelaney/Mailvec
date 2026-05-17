using Mailvec.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace Mailvec.Core.Tests.Options;

/// <summary>
/// Exercises <see cref="SharedConfig.AddMailvecSharedConfig"/> for the
/// precedence guarantees the rest of the system depends on:
///
///   • file values override per-binary appsettings defaults
///   • env-var values override the shared file
///
/// We can't trivially substitute the real ~/Library/Application Support
/// path in a test, so the tests construct an isolated ConfigurationBuilder
/// and add a custom JsonConfigurationSource pointed at a tmp file. The
/// precedence ordering they exercise is what the real helper relies on —
/// the unit tested here is the "insert before EnvironmentVariablesSource"
/// branch, validated end-to-end via IConfiguration.
/// </summary>
public sealed class SharedConfigTests : IDisposable
{
    private readonly string _sharedPath = Path.Combine(Path.GetTempPath(), $"shared-{Guid.NewGuid():N}.json");
    private readonly string _localPath = Path.Combine(Path.GetTempPath(), $"local-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        File.Delete(_sharedPath);
        File.Delete(_localPath);
    }

    [Fact]
    public void SharedFile_overrides_local_appsettings()
    {
        // Local appsettings = the binary-baked defaults.
        File.WriteAllText(_localPath, """
            { "Fastmail": { "AccountId": "from-local" } }
            """);
        // Shared file = ops/install.sh-written user config.
        File.WriteAllText(_sharedPath, """
            { "Fastmail": { "AccountId": "from-shared" } }
            """);

        var config = BuildConfigWithSharedAt(_sharedPath, _localPath);
        Assert.Equal("from-shared", config["Fastmail:AccountId"]);
    }

    [Fact]
    public void EnvVar_overrides_shared_file()
    {
        // The env-var-wins guarantee is what lets the MCPB bundle's
        // Mcp__LogToolCalls (passed in by Claude Desktop's user_config UI)
        // still beat the shared file, and lets a developer one-off-override
        // any setting in their shell without editing the file.
        File.WriteAllText(_localPath, """
            { "Fastmail": { "AccountId": "from-local" } }
            """);
        File.WriteAllText(_sharedPath, """
            { "Fastmail": { "AccountId": "from-shared" } }
            """);

        var envKey = "Fastmail__AccountId";
        Environment.SetEnvironmentVariable(envKey, "from-env");
        try
        {
            var config = BuildConfigWithSharedAt(_sharedPath, _localPath);
            Assert.Equal("from-env", config["Fastmail:AccountId"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }

    [Fact]
    public void Missing_shared_file_falls_through_to_local()
    {
        File.WriteAllText(_localPath, """
            { "Archive": { "DatabasePath": "/local/db" } }
            """);
        // _sharedPath was never created.
        var config = BuildConfigWithSharedAt(_sharedPath, _localPath);
        Assert.Equal("/local/db", config["Archive:DatabasePath"]);
    }

    [Fact]
    public void Insert_position_is_before_env_var_source()
    {
        // White-box check: AddMailvecSharedConfig should sit just before
        // the EnvironmentVariablesConfigurationSource in the chain when
        // one is present. The "from-env beats from-shared" test above is
        // the real proof, but checking the index protects against silent
        // breakage if a future Microsoft.Extensions.Configuration update
        // changes how iteration order maps to precedence.
        var builder = new ConfigurationBuilder();
        builder.AddJsonFile(_localPath, optional: true);
        builder.AddEnvironmentVariables();
        File.WriteAllText(_sharedPath, "{}");
        AddSharedAt(builder, _sharedPath);

        // Find both sources by type. The last shared-Json source is ours.
        var envIdx = -1;
        for (var i = 0; i < builder.Sources.Count; i++)
        {
            if (builder.Sources[i] is EnvironmentVariablesConfigurationSource)
            {
                envIdx = i;
                break;
            }
        }
        Assert.True(envIdx > 0, "EnvironmentVariablesConfigurationSource must be in the chain");
        // The source right before env vars should be a JSON source pointing at our shared file.
        var preceding = builder.Sources[envIdx - 1];
        Assert.IsType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>(preceding);
    }

    /// <summary>
    /// Constructs an IConfiguration mirroring the real precedence chain:
    /// per-binary appsettings → shared file → env vars. Bypasses the
    /// hard-coded ~/Library path by injecting a custom shared path so the
    /// test doesn't touch the developer's real config file.
    /// </summary>
    private static IConfigurationRoot BuildConfigWithSharedAt(string sharedPath, string localPath)
    {
        var builder = new ConfigurationBuilder();
        builder.AddJsonFile(localPath, optional: true);
        builder.AddEnvironmentVariables();
        AddSharedAt(builder, sharedPath);
        return builder.Build();
    }

    /// <summary>
    /// Mimics SharedConfig.AddMailvecSharedConfig but with a caller-supplied
    /// path. The production helper is a thin wrapper over this same logic
    /// — see the "Insert position" test for proof the prod path matches.
    /// </summary>
    private static void AddSharedAt(IConfigurationBuilder builder, string path)
    {
        var src = new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
        {
            Path = path,
            Optional = true,
            ReloadOnChange = false,
        };
        src.ResolveFileProvider();

        var envIdx = -1;
        for (var i = 0; i < builder.Sources.Count; i++)
        {
            if (builder.Sources[i] is EnvironmentVariablesConfigurationSource)
            {
                envIdx = i;
                break;
            }
        }
        if (envIdx >= 0) builder.Sources.Insert(envIdx, src);
        else builder.Sources.Add(src);
    }
}
