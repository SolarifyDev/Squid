namespace Squid.Tentacle.Platform;

/// <summary>
/// Writes a file via temp + rename so a crash mid-write never leaves a
/// half-written/corrupted target file. Used by <c>InstanceRegistry</c> and
/// <c>TentacleConfigFile</c> where the file is the sole source of truth and
/// corruption means data loss.
///
/// On most Linux/macOS filesystems, <c>File.Move(temp, target, overwrite: true)</c>
/// is an atomic <c>rename(2)</c> call. On Windows, <c>MoveFileExW</c> with
/// <c>MOVEFILE_REPLACE_EXISTING</c> is also atomic when both paths are on the
/// same volume (which they are, since the temp file is in the same directory).
/// </summary>
public static class AtomicFileWriter
{
    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="targetPath"/> atomically.
    /// If the process crashes mid-write, <paramref name="targetPath"/> retains its
    /// previous content (or doesn't exist if it was new).
    /// </summary>
    public static void WriteAllText(string targetPath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(content);

        var dir = Path.GetDirectoryName(targetPath);

        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Temp file in the same directory so rename is a same-volume atomic op.
        var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            // Belt-and-suspenders: if Move failed (shouldn't), clean up the temp.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* non-fatal */ }
        }
    }

    /// <summary>
    /// Same as <see cref="WriteAllText"/> but also tightens Unix permissions
    /// to owner-only (0600). For config files that may contain API keys or
    /// thumbprints.
    /// </summary>
    public static void WriteAllTextRestricted(string targetPath, string content)
    {
        WriteAllText(targetPath, content);
        TryRestrictPermissions(targetPath);
    }

    private static void TryRestrictPermissions(string path)
    {
        // P1-Phase12.A.1 (Windows Tentacle foundations): pre-fix this was
        // a Windows-skip + raw File.SetUnixFileMode. The new abstraction
        // ALSO hardens on Windows — break ACL inheritance so a sibling
        // user can't read the secret-bearing file. Linux behaviour is
        // bit-for-bit preserved (UnixFilePermissionManager wraps the
        // same File.SetUnixFileMode call with the same 0600 mode).
        FilePermissionManagerFactory.Resolve().RestrictToOwner(path, isDirectory: false);
    }
}
