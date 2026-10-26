namespace Mailvec.Core;

/// <summary>
/// Creates files and directories that carry mail PII at owner-only modes from
/// the first byte, rather than at the process umask. Docker's default umask is
/// 022, so a plain <c>File.WriteAllText</c> on a bind mount is a 0644 file any
/// local account on the host can read. The archive, the logs and the export
/// snapshots are already owner-only; this is the same rule for the writers
/// that create files directly (the mark-read queue, the attachment download
/// directory). Setting the mode at create, not with a chmod afterwards, leaves
/// no window in which the file is readable.
/// </summary>
public static class OwnerOnlyFile
{
    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Opens <paramref name="path"/> for writing with <c>O_CREAT|O_EXCL</c>
    /// semantics (an existing path, a symlink included, is refused rather
    /// than followed), created 0600. The mode is ignored on Windows.
    /// </summary>
    public static FileStream CreateNew(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = System.IO.FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = FileMode;
        return new FileStream(path, options);
    }

    /// <summary>
    /// Creates <paramref name="path"/> (and any missing parents) at 0700. An
    /// existing directory is left as it is: it may be one the operator chose
    /// and shares on purpose, like <c>~/Downloads</c>.
    /// </summary>
    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, DirectoryMode);
    }
}
