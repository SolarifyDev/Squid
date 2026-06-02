namespace Squid.Tentacle.Platform;

/// <summary>
/// Versioned ("blue-green") install layout for the Tentacle. The binary lives in a
/// per-version directory under <c>{installDir}/versions/{version}</c>, and the running
/// version is selected by a stable <c>{installDir}/current</c> pointer (a symlink on
/// Linux/macOS, a directory junction on Windows). The service is registered against the
/// pointer, so an upgrade activates a new version by atomically repointing
/// <c>current</c> — the previously-running version directory is never moved, overwritten,
/// or deleted during the upgrade, so any failure leaves the old version byte-for-byte
/// intact and instantly restorable by repointing back.
/// </summary>
///
/// <remarks>
/// <para>This type is the single source of truth for the layout. The install scripts
/// (<c>install-tentacle.sh</c> / <c>.ps1</c>), the upgrade scripts, and service
/// registration all mirror these names — drift detectors pin the script copies against
/// these literals.</para>
/// <para>Existing "flat" installs (binary directly in <c>{installDir}</c>) are detected
/// via <see cref="IsVersioned"/> and left unchanged; callers fall back to today's
/// behaviour so nothing breaks before an install migrates.</para>
/// </remarks>
public static class TentacleLayout
{
    /// <summary>Directory under the install root that holds per-version subdirectories.</summary>
    public const string VersionsDirName = "versions";

    /// <summary>Stable pointer (symlink/junction) under the install root selecting the active version.</summary>
    public const string CurrentPointerName = "current";

    /// <summary>The published binary filename — platform-specific extension. NOT the "squid-tentacle" shell wrapper.</summary>
    public static string BinaryFileName => PlatformPaths.IsWindows ? "Squid.Tentacle.exe" : "Squid.Tentacle";

    /// <summary><c>{installDir}/versions</c> — parent of all per-version directories.</summary>
    public static string VersionsDir(string installDir) => Path.Combine(installDir, VersionsDirName);

    /// <summary><c>{installDir}/versions/{version}</c> — a specific version's directory.</summary>
    public static string VersionDir(string installDir, string version) => Path.Combine(installDir, VersionsDirName, version);

    /// <summary><c>{installDir}/current</c> — the stable pointer to the active version.</summary>
    public static string CurrentPointer(string installDir) => Path.Combine(installDir, CurrentPointerName);

    /// <summary>Stable binary path the service execs — resolved through the <c>current</c> pointer.</summary>
    public static string PointerBinaryPath(string installDir) => Path.Combine(CurrentPointer(installDir), BinaryFileName);

    /// <summary>Binary path inside a specific version directory.</summary>
    public static string VersionBinaryPath(string installDir, string version) => Path.Combine(VersionDir(installDir, version), BinaryFileName);

    /// <summary>
    /// Given the directory the running binary loaded from, returns the install root if
    /// this is a versioned layout, otherwise <c>null</c> (flat install). Pure — no
    /// filesystem access; the caller confirms the pointer exists via <see cref="IsVersioned"/>.
    /// Recognised shapes: <c>{installRoot}/versions/{version}</c> and <c>{installRoot}/current</c>.
    /// </summary>
    public static string TryGetInstallRootFromRunningDir(string runningDir)
    {
        if (string.IsNullOrWhiteSpace(runningDir)) return null;

        var dir = runningDir.TrimEnd('/', '\\');

        if (dir.Length == 0) return null;

        var name = Path.GetFileName(dir);

        if (string.Equals(name, CurrentPointerName, StringComparison.Ordinal))
            return Path.GetDirectoryName(dir);

        var parent = Path.GetDirectoryName(dir);

        if (parent != null && string.Equals(Path.GetFileName(parent), VersionsDirName, StringComparison.Ordinal))
            return Path.GetDirectoryName(parent);

        return null;
    }

    /// <summary>
    /// True when <paramref name="installDir"/> uses the versioned layout — i.e. a
    /// <c>current</c> pointer is present. Flat installs (no pointer) return false so
    /// callers preserve today's behaviour unchanged.
    /// </summary>
    public static bool IsVersioned(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return false;

        var pointer = CurrentPointer(installDir);

        return Directory.Exists(pointer) || File.Exists(pointer);
    }
}
