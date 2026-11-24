using System.Text;

namespace Squid.Core.Setup;

/// <summary>
/// Writes files that hold secrets (configuration, setup token, generated password, PostgreSQL
/// password file): created owner-only from the first byte — never briefly world-readable — and
/// flushed to disk before an atomic rename, so a power loss cannot leave a truncated file behind.
/// </summary>
public static class OwnerOnlyFile
{
    public static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var temp = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            Write(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void Write(string path, string content)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };

        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(path, options);
        var bytes = Encoding.UTF8.GetBytes(content);

        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
