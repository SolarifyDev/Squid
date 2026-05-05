namespace Squid.Tentacle.Platform;

/// <summary>
/// picks the platform-appropriate
/// <see cref="IServiceUserProvider"/>. Static factory matching the
/// <see cref="FilePermissionManagerFactory"/> + <see cref="UpgradeStatusStorageFactory"/>
/// convention.
/// </summary>
public static class ServiceUserProviderFactory
{
    /// <summary>
    /// Linux → <see cref="LinuxServiceUserProvider"/>;
    /// Windows → <see cref="WindowsServiceUserProvider"/>;
    /// other → <see cref="NullServiceUserProvider"/>.
    /// </summary>
    public static IServiceUserProvider Resolve()
    {
        if (OperatingSystem.IsLinux())
            return new LinuxServiceUserProvider();

        if (OperatingSystem.IsWindows())
            return new WindowsServiceUserProvider();

        return new NullServiceUserProvider();
    }
}
