namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.A.2 — picks the platform-appropriate
/// <see cref="IUpgradeStatusStorage"/>. Static factory matching the
/// <see cref="FilePermissionManagerFactory"/> + <see cref="PlatformPaths"/>
/// convention in this namespace.
/// </summary>
public static class UpgradeStatusStorageFactory
{
    /// <summary>
    /// Linux → <see cref="LinuxUpgradeStatusStorage"/>;
    /// Windows → <see cref="WindowsUpgradeStatusStorage"/>;
    /// other → <see cref="NullUpgradeStatusStorage"/> (graceful empty).
    /// </summary>
    public static IUpgradeStatusStorage Resolve()
    {
        if (OperatingSystem.IsLinux())
            return new LinuxUpgradeStatusStorage();

        if (OperatingSystem.IsWindows())
            return new WindowsUpgradeStatusStorage();

        return new NullUpgradeStatusStorage();
    }
}
