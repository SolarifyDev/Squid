namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.A.1 — resolves the platform-appropriate
/// <see cref="IFilePermissionManager"/>.
///
/// <para>Static factory rather than DI registration because the call
/// sites are all in static utility classes (<c>ResilientFileSystem</c>,
/// <c>AtomicFileWriter</c>, <c>LocalScriptService</c>) where DI scope
/// resolution would be a constructor refactor for zero benefit. The
/// factory itself is testable: call <see cref="Resolve"/> and assert on
/// the returned type.</para>
///
/// <para>Pattern matches the existing <c>PlatformPaths</c> static
/// helper in the same namespace — same project convention.</para>
/// </summary>
public static class FilePermissionManagerFactory
{
    /// <summary>
    /// Returns the impl matching the running OS.
    /// Windows → <see cref="WindowsFilePermissionManager"/>;
    /// Linux + macOS → <see cref="UnixFilePermissionManager"/>.
    /// </summary>
    public static IFilePermissionManager Resolve()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsFilePermissionManager();

        return new UnixFilePermissionManager();
    }
}
