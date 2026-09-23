using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Mailvec.Core.Options;

/// <summary>
/// The rules for where credential material may come from. Two ways in are
/// allowed — an environment variable, or an owner-only file named by an
/// <c>*ApiKeyFile</c> option — and these helpers enforce both halves instead of
/// leaving it to option doc comments that nothing checks.
/// </summary>
public static class SecretConfig
{
    /// <summary>
    /// Refuse a credential that arrived from a JSON config file. Every Mailvec
    /// binary layers the SHARED <c>appsettings.Local.json</c> over its own, and
    /// that file is world-readable by design (it holds ordinary settings and is
    /// read by every service and the CLI); a per-binary <c>appsettings*.json</c>
    /// sits beside the published binary. A key in either is readable by any
    /// local account. Environment variables (compose's <c>.env</c>, a shell
    /// export) and in-memory configuration pass.
    /// </summary>
    /// <remarks>
    /// Works on the configuration's own provider list — the last provider that
    /// supplies the key is the one whose value is used — so an environment
    /// override of a key that ALSO appears in a JSON file is still accepted:
    /// the file's copy is not the one in use. That file should still be
    /// cleaned, but refusing to start over a shadowed value would punish the
    /// correct fix.
    /// </remarks>
    public static void RejectIfFromJsonFile(IConfiguration configuration, string key, string remedy)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration is not IConfigurationRoot root) return;

        foreach (var provider in root.Providers.Reverse())
        {
            if (!provider.TryGet(key, out var value) || string.IsNullOrWhiteSpace(value)) continue;
            if (provider is JsonConfigurationProvider json)
            {
                throw new InvalidOperationException(
                    $"{key} is set in a JSON config file ('{json.Source.Path}'). Mailvec's config files are " +
                    $"readable by other local accounts, so it refuses to use a credential from one. {remedy}");
            }
            return;
        }
    }

    /// <summary>
    /// Read an owner-only secret file: it must exist, be non-empty after
    /// trimming, and (on Unix) not be readable by other users or writable by
    /// anyone but its owner. Group-read is allowed — a common, deliberate
    /// shape for a service group.
    /// </summary>
    public static string ReadSecretFile(string path, string what)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"{what} '{path}' does not exist.");

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode tooOpen = UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.GroupWrite;
            if ((mode & tooOpen) != 0)
            {
                throw new InvalidOperationException(
                    $"{what} '{path}' is readable by other users or writable by more than its owner " +
                    $"(mode {Convert.ToString((int)mode, 8)}). Restrict it: chmod 600 '{path}'.");
            }
        }

        var value = File.ReadAllText(path).Trim();
        if (value.Length == 0)
            throw new InvalidOperationException($"{what} '{path}' is empty.");
        return value;
    }
}
