using System.Runtime.InteropServices;

namespace Squid.Tentacle.Platform;

/// <summary>
/// Cross-platform filesystem path conventions for Squid Tentacle.
/// Centralises all OS-specific path logic so no caller needs its own branching.
/// </summary>
///
/// <remarks>
/// <para>Precedence rule for reads: system path first (covers root / service
/// install), user path second (covers unprivileged invocations).</para>
/// <para>For writes, callers must explicitly pick either
/// <see cref="GetSystemConfigDir"/> or <see cref="GetUserConfigDir"/> based on
/// whether they have permission to write under <c>/etc</c>, <c>/Library</c>,
/// or <c>C:\ProgramData</c>.</para>
/// </remarks>
public static class PlatformPaths
{
    /// <summary>Base name used for directories and services ("squid-tentacle").</summary>
    public const string AppFolderName = "squid-tentacle";

    /// <summary>Branded folder name on platforms that nest under a vendor dir ("Squid/Tentacle").</summary>
    public const string BrandedFolderName = "Squid/Tentacle";

    public static bool IsLinux => OperatingSystem.IsLinux();
    public static bool IsMacOS => OperatingSystem.IsMacOS();
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>
    /// System-level configuration directory. Requires elevation (root / Administrator) to write.
    /// Returns the canonical per-OS location where daemons and system services expect their config.
    /// </summary>
    public static string GetSystemConfigDir()
    {
        if (IsLinux) return $"/etc/{AppFolderName}";

        if (IsMacOS) return $"/Library/Application Support/{BrandedFolderName}";

        if (IsWindows)
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, BrandedFolderName);
        }

        throw new PlatformNotSupportedException($"Unsupported OS: {RuntimeInformation.OSDescription}");
    }

    /// <summary>
    /// User-level configuration directory — writable without elevation.
    /// Used as fallback when the current process can't touch <see cref="GetSystemConfigDir"/>.
    /// </summary>
    public static string GetUserConfigDir()
    {
        if (IsLinux)
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

            if (!string.IsNullOrWhiteSpace(xdg))
                return Path.Combine(xdg, AppFolderName);

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config", AppFolderName);
        }

        if (IsMacOS)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library/Application Support", BrandedFolderName);
        }

        if (IsWindows)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, BrandedFolderName);
        }

        throw new PlatformNotSupportedException($"Unsupported OS: {RuntimeInformation.OSDescription}");
    }

    /// <summary>
    /// Default install directory for a <c>binary + systemd/service</c> deployment.
    /// Docker / dotnet-run invocations typically override this with WorkingDirectory.
    /// </summary>
    public static string GetDefaultInstallDir()
    {
        if (IsLinux) return $"/opt/{AppFolderName}";

        if (IsMacOS) return $"/usr/local/{AppFolderName}";

        if (IsWindows)
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return Path.Combine(programFiles, BrandedFolderName.Replace('/', Path.DirectorySeparatorChar));
        }

        throw new PlatformNotSupportedException($"Unsupported OS: {RuntimeInformation.OSDescription}");
    }

    /// <summary>
    /// Returns <see cref="GetSystemConfigDir"/> when it exists or is writable;
    /// otherwise falls back to <see cref="GetUserConfigDir"/>. Used by reads
    /// that don't care which scope they land in.
    /// </summary>
    public static string ResolveActiveConfigDir()
    {
        var system = GetSystemConfigDir();

        if (Directory.Exists(system) && IsDirectoryAccessible(system))
            return system;

        return GetUserConfigDir();
    }

    /// <summary>
    /// Picks the "best" scope to write into: system if the current process can write there
    /// (typically when running as root/Administrator), user otherwise. Callers should prefer
    /// this over hardcoding either choice unless they have an explicit reason (e.g. <c>sudo</c>).
    /// </summary>
    public static string PickWritableConfigDir()
    {
        var system = GetSystemConfigDir();

        if (TryEnsureDirectory(system))
            return system;

        var user = GetUserConfigDir();
        Directory.CreateDirectory(user);
        return user;
    }

    public static string GetInstancesRegistryPath(string configDir) =>
        Path.Combine(configDir, "instances.json");

    public static string GetInstanceConfigPath(string configDir, string instanceName) =>
        Path.Combine(configDir, "instances", $"{instanceName}.config.json");

    public static string GetInstanceCertsDir(string configDir, string instanceName) =>
        Path.Combine(configDir, "instances", instanceName, "certs");

    private static bool IsDirectoryAccessible(string path)
    {
        try
        {
            Directory.EnumerateFileSystemEntries(path).Take(0).ToList();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryEnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);

            // Probe writability with a short-lived temp file
            var probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
