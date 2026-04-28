using Serilog;

namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.A.1 — Linux + macOS implementation of
/// <see cref="IFilePermissionManager"/>.
///
/// <para>Wraps <see cref="File.SetUnixFileMode"/> directly. Pre-Phase-12
/// behaviour preserved exactly — every call site that previously did
/// <c>File.SetUnixFileMode(path, 0xxx)</c> now goes through this
/// adapter without any observable change on Linux.</para>
/// </summary>
public sealed class UnixFilePermissionManager : IFilePermissionManager
{
    public void RestrictToOwner(string path, bool isDirectory = false)
    {
        try
        {
            // 0700 for directories (owner needs +x to traverse), 0600 for files.
            var mode = isDirectory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;

            // File.SetUnixFileMode is documented to throw FileNotFoundException
            // for missing paths — defensive catch handles the race with
            // concurrent cleanup.
            if (isDirectory ? Directory.Exists(path) : File.Exists(path))
                File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[FilePermissionManager] RestrictToOwner({Path}, isDirectory={IsDir}) failed",
                path, isDirectory);
        }
    }

    public void TrySetUnixMode(string path, UnixFileMode mode)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[FilePermissionManager] TrySetUnixMode({Path}, {Mode}) failed", path, mode);
        }
    }
}
