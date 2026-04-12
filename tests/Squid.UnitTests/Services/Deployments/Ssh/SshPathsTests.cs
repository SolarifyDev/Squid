using Squid.Core.Services.DeploymentExecution.Ssh;

namespace Squid.UnitTests.Services.Deployments.Ssh;

public class SshPathsTests
{
    [Theory]
    [InlineData(1, null, ".squid/Work/1")]
    [InlineData(42, null, ".squid/Work/42")]
    [InlineData(99999, null, ".squid/Work/99999")]
    [InlineData(1, "", ".squid/Work/1")]
    [InlineData(1, "  ", ".squid/Work/1")]
    public void WorkDirectory_DefaultBase_ReturnsExpectedPath(int serverTaskId, string resolvedBaseDir, string expected)
    {
        SshPaths.WorkDirectory(serverTaskId, resolvedBaseDir).ShouldBe(expected);
    }

    [Theory]
    [InlineData(1, "/opt/squid", "/opt/squid/Work/1")]
    [InlineData(42, "/home/deploy/.squid-custom", "/home/deploy/.squid-custom/Work/42")]
    [InlineData(1, "/opt/squid/", "/opt/squid/Work/1")]
    [InlineData(1, "/home/user/.squid", "/home/user/.squid/Work/1")]
    public void WorkDirectory_CustomBase_ReturnsExpectedPath(int serverTaskId, string resolvedBaseDir, string expected)
    {
        SshPaths.WorkDirectory(serverTaskId, resolvedBaseDir).ShouldBe(expected);
    }

    [Theory]
    [InlineData(".squid/Work/1", "script.sh", ".squid/Work/1/script.sh")]
    [InlineData(".squid/Work/42", "deploy.yaml", ".squid/Work/42/deploy.yaml")]
    [InlineData("/opt/squid/Work/1", "script.ps1", "/opt/squid/Work/1/script.ps1")]
    public void ScriptPath_ReturnsExpectedPath(string workDir, string scriptName, string expected)
    {
        SshPaths.ScriptPath(workDir, scriptName).ShouldBe(expected);
    }

    // ========================================================================
    // ResolveBaseDirectory
    // ========================================================================

    [Theory]
    [InlineData("/opt/deploy")]
    [InlineData("/opt/deploy/")]
    public void ResolveBaseDirectory_CustomWorkDir_ReturnsCustomPath(string remoteWorkDir)
    {
        var sshClient = new Mock<Renci.SshNet.SshClient>("localhost", "user", "pass") { CallBase = false };

        var result = SshPaths.ResolveBaseDirectory(sshClient.Object, remoteWorkDir);

        result.ShouldBe("/opt/deploy");
    }

    [Fact]
    public void ResolveHomeDirectory_InvalidOutput_ReturnsEmpty()
    {
        // ResolveHomeDirectory returns empty when output doesn't start with /
        var result = SshPaths.ResolveHomeDirectory(new Mock<Renci.SshNet.SshClient>("localhost", "user", "pass") { CallBase = false }.Object);

        // Mock SshClient can't actually execute commands, so it should return empty
        result.ShouldBe(string.Empty);
    }

    // ========================================================================
    // Package Paths
    // ========================================================================

    [Theory]
    [InlineData("/opt/squid", "/opt/squid/Packages")]
    [InlineData("/home/user/.squid", "/home/user/.squid/Packages")]
    [InlineData("~/.squid", "~/.squid/Packages")]
    public void PackageCacheDirectory_ReturnsExpectedPath(string baseDir, string expected)
    {
        SshPaths.PackageCacheDirectory(baseDir).ShouldBe(expected);
    }

    [Theory]
    [InlineData("/opt/squid", "MyApp", "1.0.0", "/opt/squid/Packages/MyApp.1.0.0.nupkg")]
    [InlineData("/home/deploy/.squid", "nginx", "1.21.0", "/home/deploy/.squid/Packages/nginx.1.21.0.nupkg")]
    [InlineData("~/.squid", "pkg.with.dots", "2.0.0-beta", "~/.squid/Packages/pkg.with.dots.2.0.0-beta.nupkg")]
    public void PackageNupkgPath_ReturnsExpectedPath(string baseDir, string packageId, string version, string expected)
    {
        SshPaths.PackageNupkgPath(baseDir, packageId, version).ShouldBe(expected);
    }

    [Theory]
    [InlineData("/opt/squid", "MyApp", "1.0.0", "/opt/squid/Packages/MyApp.1.0.0")]
    [InlineData("/home/deploy/.squid", "nginx", "1.21.0", "/home/deploy/.squid/Packages/nginx.1.21.0")]
    [InlineData("~/.squid", "pkg", "3.0.0-rc1", "~/.squid/Packages/pkg.3.0.0-rc1")]
    public void PackageExtractDir_ReturnsExpectedPath(string baseDir, string packageId, string version, string expected)
    {
        SshPaths.PackageExtractDir(baseDir, packageId, version).ShouldBe(expected);
    }

    [Fact]
    public void PackageNupkgPath_AndPackageExtractDir_AreDistinct()
    {
        var baseDir = "/opt/squid";
        var packageId = "MyApp";
        var version = "1.0.0";

        SshPaths.PackageNupkgPath(baseDir, packageId, version).ShouldNotBe(SshPaths.PackageExtractDir(baseDir, packageId, version));
    }

    [Fact]
    public void PackageCacheDirectory_IsPrefixOfPackageNupkgPath()
    {
        var baseDir = "/opt/squid";
        var packageId = "MyApp";
        var version = "1.0.0";

        var cacheDir = SshPaths.PackageCacheDirectory(baseDir);
        var nupkgPath = SshPaths.PackageNupkgPath(baseDir, packageId, version);

        nupkgPath.ShouldStartWith(cacheDir);
    }

    [Fact]
    public void PackageCacheDirectory_IsPrefixOfPackageExtractDir()
    {
        var baseDir = "/opt/squid";
        var packageId = "MyApp";
        var version = "1.0.0";

        var cacheDir = SshPaths.PackageCacheDirectory(baseDir);
        var extractDir = SshPaths.PackageExtractDir(baseDir, packageId, version);

        extractDir.ShouldStartWith(cacheDir);
    }
}
