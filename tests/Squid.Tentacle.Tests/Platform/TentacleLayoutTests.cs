using System.IO;
using Squid.Tentacle.Platform;

namespace Squid.Tentacle.Tests.Platform;

/// <summary>
/// Pins the versioned ("blue-green") install layout contract. This is the single
/// source of truth that install scripts, the upgrade scripts, and service
/// registration all mirror — drift here breaks atomic-pointer upgrades, so every
/// path component and the flat-vs-versioned detection is hard-pinned.
/// </summary>
public sealed class TentacleLayoutTests : IDisposable
{
    private readonly string _tempDir;

    public TentacleLayoutTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"squid-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Contract constants ──────────────────────────────────────────────────

    [Fact]
    public void VersionsDirName_IsPinned()
    {
        // Renaming breaks every install/upgrade script that mirrors this literal.
        TentacleLayout.VersionsDirName.ShouldBe("versions");
    }

    [Fact]
    public void CurrentPointerName_IsPinned()
    {
        TentacleLayout.CurrentPointerName.ShouldBe("current");
    }

    [Fact]
    public void BinaryFileName_MatchesPublishedNamePerOs()
    {
        // .NET publish output is "Squid.Tentacle(.exe)" — NOT the "squid-tentacle"
        // shell wrapper. Wrong name = service registered against a nonexistent path.
        var expected = OperatingSystem.IsWindows() ? "Squid.Tentacle.exe" : "Squid.Tentacle";
        TentacleLayout.BinaryFileName.ShouldBe(expected);
    }

    // ── Pure path composition ───────────────────────────────────────────────

    [Fact]
    public void VersionsDir_IsInstallDirSlashVersions()
    {
        var root = Path.Combine("opt", "squid-tentacle");
        TentacleLayout.VersionsDir(root).ShouldBe(Path.Combine(root, "versions"));
    }

    [Fact]
    public void VersionDir_NestsVersionUnderVersions()
    {
        var root = Path.Combine("opt", "squid-tentacle");
        TentacleLayout.VersionDir(root, "1.8.7").ShouldBe(Path.Combine(root, "versions", "1.8.7"));
    }

    [Fact]
    public void CurrentPointer_IsInstallDirSlashCurrent()
    {
        var root = Path.Combine("opt", "squid-tentacle");
        TentacleLayout.CurrentPointer(root).ShouldBe(Path.Combine(root, "current"));
    }

    [Fact]
    public void PointerBinaryPath_RunsThroughCurrentPointer()
    {
        var root = Path.Combine("opt", "squid-tentacle");
        TentacleLayout.PointerBinaryPath(root)
            .ShouldBe(Path.Combine(root, "current", TentacleLayout.BinaryFileName));
    }

    [Fact]
    public void VersionBinaryPath_IsBinaryInsideVersionDir()
    {
        var root = Path.Combine("opt", "squid-tentacle");
        TentacleLayout.VersionBinaryPath(root, "1.8.7")
            .ShouldBe(Path.Combine(root, "versions", "1.8.7", TentacleLayout.BinaryFileName));
    }

    // ── TryGetInstallRootFromRunningDir (pure) ──────────────────────────────

    [Fact]
    public void TryGetInstallRoot_FromVersionDir_ReturnsRoot()
    {
        var root = Path.Combine(Path.DirectorySeparatorChar.ToString(), "opt", "squid-tentacle");
        var running = Path.Combine(root, "versions", "1.8.7");

        TentacleLayout.TryGetInstallRootFromRunningDir(running).ShouldBe(root);
    }

    [Fact]
    public void TryGetInstallRoot_FromCurrentPointer_ReturnsRoot()
    {
        var root = Path.Combine(Path.DirectorySeparatorChar.ToString(), "opt", "squid-tentacle");
        var running = Path.Combine(root, "current");

        TentacleLayout.TryGetInstallRootFromRunningDir(running).ShouldBe(root);
    }

    [Fact]
    public void TryGetInstallRoot_TrailingSeparator_StillResolves()
    {
        var root = Path.Combine(Path.DirectorySeparatorChar.ToString(), "opt", "squid-tentacle");
        var running = Path.Combine(root, "versions", "1.8.7") + Path.DirectorySeparatorChar;

        TentacleLayout.TryGetInstallRootFromRunningDir(running).ShouldBe(root);
    }

    [Fact]
    public void TryGetInstallRoot_FlatInstall_ReturnsNull()
    {
        // Binary sits directly in the install dir (today's layout) — no versioned root.
        var root = Path.Combine(Path.DirectorySeparatorChar.ToString(), "opt", "squid-tentacle");

        TentacleLayout.TryGetInstallRootFromRunningDir(root).ShouldBeNull();
    }

    [Fact]
    public void TryGetInstallRoot_UnrelatedSubdir_ReturnsNull()
    {
        var root = Path.Combine(Path.DirectorySeparatorChar.ToString(), "opt", "squid-tentacle");
        var running = Path.Combine(root, "lib");

        TentacleLayout.TryGetInstallRootFromRunningDir(running).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryGetInstallRoot_EmptyOrNull_ReturnsNull(string running)
    {
        TentacleLayout.TryGetInstallRootFromRunningDir(running).ShouldBeNull();
    }

    // ── IsVersioned (filesystem) ────────────────────────────────────────────

    [Fact]
    public void IsVersioned_FlatInstall_NoPointer_ReturnsFalse()
    {
        var install = Path.Combine(_tempDir, "flat");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, TentacleLayout.BinaryFileName), "binary");

        TentacleLayout.IsVersioned(install).ShouldBeFalse();
    }

    [Fact]
    public void IsVersioned_CurrentPointerPresent_ReturnsTrue()
    {
        var install = Path.Combine(_tempDir, "versioned");
        Directory.CreateDirectory(TentacleLayout.VersionDir(install, "1.8.7"));
        // A real directory named "current" satisfies the "pointer exists" predicate;
        // the install scripts create it as a symlink/junction (covered by E2E).
        Directory.CreateDirectory(TentacleLayout.CurrentPointer(install));

        TentacleLayout.IsVersioned(install).ShouldBeTrue();
    }

    [Fact]
    public void IsVersioned_CurrentSymlinkToVersionDir_ReturnsTrue()
    {
        // Symlink creation is unprivileged on Linux/macOS (where unit tests run);
        // on Windows it needs elevation, so skip there — the dir case above covers
        // the predicate and the install-script E2E covers the real junction.
        if (OperatingSystem.IsWindows()) return;

        var install = Path.Combine(_tempDir, "symlinked");
        var versionDir = TentacleLayout.VersionDir(install, "1.8.7");
        Directory.CreateDirectory(versionDir);
        Directory.CreateSymbolicLink(TentacleLayout.CurrentPointer(install), versionDir);

        TentacleLayout.IsVersioned(install).ShouldBeTrue();
    }
}
