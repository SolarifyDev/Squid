using System.IO;
using Squid.Tentacle.Platform;

namespace Squid.Tentacle.Tests.Platform;

/// <summary>
/// Rule 12.5 drift detector. The install scripts (<c>deploy/scripts/install-tentacle.sh</c>
/// and <c>.ps1</c>) hard-code the versioned-layout directory names that
/// <see cref="TentacleLayout"/> defines in C#. There is no compile-time link between the
/// shell/PowerShell literals and the C# constants, so this test fails if either side
/// drifts — e.g. renaming <see cref="TentacleLayout.VersionsDirName"/> without updating
/// the scripts would silently break atomic-pointer upgrades.
///
/// It also pins the load-bearing safety mechanics (atomic `current` swap on Linux,
/// unprivileged junction on Windows) and the best-effort flat fallback, so a refactor
/// can't quietly drop them.
/// </summary>
public sealed class InstallScriptVersionedLayoutDriftTests
{
    private static string RepoRoot()
    {
        // Walk up from the test binary dir until we find .git — stable from any cwd.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate .git — test must run inside the Squid repo working tree");
    }

    private static string ReadScript(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), relativePath);
        File.Exists(path).ShouldBeTrue($"Install script not found at {path}");

        return File.ReadAllText(path);
    }

    private static string LinuxScript() => ReadScript(Path.Combine("deploy", "scripts", "install-tentacle.sh"));

    private static string WindowsScript() => ReadScript(Path.Combine("deploy", "scripts", "install-tentacle.ps1"));

    // ── Contract-name drift (the primary guard) ─────────────────────────────

    [Fact]
    public void LinuxScript_UsesTentacleLayoutDirNames()
    {
        var sh = LinuxScript();

        sh.Contains($"$INSTALL_DIR/{TentacleLayout.VersionsDirName}").ShouldBeTrue(
            $"install-tentacle.sh must place versions under '{TentacleLayout.VersionsDirName}' to match TentacleLayout.VersionsDirName. If you renamed the C# constant, update the script too.");
        sh.Contains($"$INSTALL_DIR/{TentacleLayout.CurrentPointerName}").ShouldBeTrue(
            $"install-tentacle.sh must use the '{TentacleLayout.CurrentPointerName}' pointer to match TentacleLayout.CurrentPointerName.");
    }

    [Fact]
    public void WindowsScript_UsesTentacleLayoutDirNames()
    {
        var ps = WindowsScript();

        ps.Contains($"'{TentacleLayout.VersionsDirName}'").ShouldBeTrue(
            $"install-tentacle.ps1 must Join-Path the '{TentacleLayout.VersionsDirName}' dir to match TentacleLayout.VersionsDirName.");
        ps.Contains($"'{TentacleLayout.CurrentPointerName}'").ShouldBeTrue(
            $"install-tentacle.ps1 must Join-Path the '{TentacleLayout.CurrentPointerName}' pointer to match TentacleLayout.CurrentPointerName.");
    }

    // ── Safety mechanics (atomic swap + unprivileged junction) ──────────────

    [Fact]
    public void LinuxScript_SwapsCurrentPointerAtomically()
    {
        var sh = LinuxScript();

        // `mv -T` onto an existing symlink is the atomic rename that activates a version
        // without a window where `current` is absent. Losing it reintroduces a race.
        sh.Contains("mv -T").ShouldBeTrue(
            "install-tentacle.sh must repoint `current` via `mv -T` (atomic symlink replace). Without -T, mv moves INTO the target dir.");
        sh.Contains("ln -sfn").ShouldBeTrue(
            "install-tentacle.sh must create the `current` symlink with `ln -sfn`.");
    }

    [Fact]
    public void WindowsScript_UsesJunctionAndSafeDelete()
    {
        var ps = WindowsScript();

        // Junctions need no elevation (unlike symlinks) — required for user-dir installs.
        ps.Contains("-ItemType Junction").ShouldBeTrue(
            "install-tentacle.ps1 must create `current` as a directory Junction (unprivileged, unlike symlinks).");
        // Non-recursive delete removes ONLY the reparse point; Remove-Item -Recurse on a
        // junction can delete the TARGET version's files — a data-loss footgun.
        ps.Contains("[System.IO.Directory]::Delete(").ShouldBeTrue(
            "install-tentacle.ps1 must delete the old `current` junction non-recursively to avoid wiping the target version's files.");
    }

    // ── Best-effort flat fallback (non-breaking guarantee) ──────────────────

    [Fact]
    public void BothScripts_KeepFlatFallback_WhenVersionUnresolved()
    {
        // If the freshly-extracted binary can't report its version, both scripts must
        // fall back to a flat install (today's behaviour) rather than fail — so a missing
        // runtime dep never turns a previously-successful install into a hard failure.
        LinuxScript().Contains("flat layout").ShouldBeTrue(
            "install-tentacle.sh must keep the flat-layout fallback when the version can't be resolved.");
        WindowsScript().Contains("flat layout").ShouldBeTrue(
            "install-tentacle.ps1 must keep the flat-layout fallback when the version can't be resolved.");
    }
}
