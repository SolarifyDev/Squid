using System.IO;
using System.Runtime.InteropServices;
using Squid.Tentacle.Platform;

namespace Squid.Tentacle.Tests.Platform;

public class PlatformPathsTests
{
    [Fact]
    public void GetSystemConfigDir_IsPlatformSpecific()
    {
        var path = PlatformPaths.GetSystemConfigDir();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            path.ShouldBe("/etc/squid-tentacle");
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            path.ShouldBe("/Library/Application Support/Squid/Tentacle");
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            path.ShouldEndWith("Squid\\Tentacle");
    }

    [Fact]
    public void GetUserConfigDir_IsUnderUserHome_OrXdgConfigHome()
    {
        var path = PlatformPaths.GetUserConfigDir();

        path.ShouldNotBeNullOrWhiteSpace();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            path.ShouldContain("squid-tentacle");
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            path.ShouldContain("Library");
    }

    [Fact]
    public void GetDefaultInstallDir_IsPlatformSpecific()
    {
        var path = PlatformPaths.GetDefaultInstallDir();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            path.ShouldBe("/opt/squid-tentacle");
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            path.ShouldBe("/usr/local/squid-tentacle");
    }

    [Fact]
    public void GetInstanceConfigPath_ProducesConventionalLayout()
    {
        var path = PlatformPaths.GetInstanceConfigPath("/etc/squid-tentacle", "production");

        path.ShouldBe(Path.Combine("/etc/squid-tentacle", "instances", "production.config.json"));
    }

    [Fact]
    public void GetInstanceCertsDir_ProducesPerInstanceDir()
    {
        var path = PlatformPaths.GetInstanceCertsDir("/etc/squid-tentacle", "production");

        path.ShouldBe(Path.Combine("/etc/squid-tentacle", "instances", "production", "certs"));
    }

    [Fact]
    public void GetInstancesRegistryPath_IsInstancesJsonAtRoot()
    {
        var path = PlatformPaths.GetInstancesRegistryPath("/etc/squid-tentacle");

        path.ShouldBe(Path.Combine("/etc/squid-tentacle", "instances.json"));
    }

    [Fact]
    public void PickWritableConfigDir_ReturnsAWritablePath()
    {
        // This runs on the test machine — we can't assume root, but the method must
        // always return *some* path the current process can write to (user dir fallback).
        var path = PlatformPaths.PickWritableConfigDir();

        path.ShouldNotBeNullOrWhiteSpace();
        Directory.Exists(path).ShouldBeTrue();

        // Round-trip write probe
        var probe = Path.Combine(path, $".test-{Guid.NewGuid():N}");
        File.WriteAllText(probe, "x");
        File.Delete(probe);
    }
}
