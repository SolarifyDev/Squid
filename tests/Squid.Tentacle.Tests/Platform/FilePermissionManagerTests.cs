using Shouldly;
using Squid.Tentacle.Platform;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Platform;

/// <summary>
/// P1-Phase12.A.1 (Windows Tentacle foundations) — pin the file-permission
/// abstraction contract.
///
/// <para><b>Why this exists</b>: pre-Phase-12 the agent had 6+ scattered
/// call sites of <c>File.SetUnixFileMode(path, mode)</c>, each gated by
/// <c>OperatingSystem.IsWindows()</c>. As we add Windows Tentacle support,
/// the secret-bearing paths (PFX, encrypted config) need genuine ACL
/// hardening on Windows — not a no-op skip. Other paths (workspace
/// scripts, asset files) tolerate Windows' default inherited ACL.</para>
///
/// <para>Two-method interface: <c>RestrictToOwner</c> is the
/// security-bearing path (Linux 0600 / Windows ACL break-inheritance +
/// Owner FullControl); <c>TrySetUnixMode</c> is the legacy pass-through
/// (no-op on Windows).</para>
///
/// <para>Tests run on whatever OS the CI runner is — assertions
/// platform-gate via <c>OperatingSystem.IsWindows()</c>.</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class FilePermissionManagerTests : IDisposable
{
    private readonly string _tempRoot;

    public FilePermissionManagerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "squid-permmgr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // ── Factory selection ──────────────────────────────────────────────────

    [Fact]
    public void Resolve_ReturnsCorrectImplForCurrentPlatform()
    {
        var mgr = FilePermissionManagerFactory.Resolve();

        if (OperatingSystem.IsWindows())
            mgr.ShouldBeOfType<WindowsFilePermissionManager>();
        else
            mgr.ShouldBeOfType<UnixFilePermissionManager>();
    }

    // ── RestrictToOwner — file path ────────────────────────────────────────

    [Fact]
    public void RestrictToOwner_File_LinuxModeIs600()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var path = Path.Combine(_tempRoot, "secret.pfx");
        File.WriteAllText(path, "fake-pfx-bytes");

        new UnixFilePermissionManager().RestrictToOwner(path, isDirectory: false);

        var mode = File.GetUnixFileMode(path);
        // 0600 = UserRead | UserWrite. Group/Other should have NONE.
        mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite, customMessage:
            "Secret-bearing files MUST be 0600 on Unix — pre-Phase-12 AtomicFileWriter set this exact mode.");
    }

    [Fact]
    public void RestrictToOwner_Directory_LinuxModeIs700()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var path = Path.Combine(_tempRoot, "secret-dir");
        Directory.CreateDirectory(path);

        new UnixFilePermissionManager().RestrictToOwner(path, isDirectory: true);

        var mode = File.GetUnixFileMode(path);
        // 0700 = UserRead | UserWrite | UserExecute. Need execute bit on dirs to traverse.
        mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void RestrictToOwner_NonexistentPath_DoesNotThrow()
    {
        // Defensive — security tightening is best-effort. A removed file
        // (race with concurrent cleanup) must NOT crash the deploy flow.
        var mgr = FilePermissionManagerFactory.Resolve();

        Should.NotThrow(() => mgr.RestrictToOwner(Path.Combine(_tempRoot, "ghost.txt")));
    }

    // ── TrySetUnixMode — legacy pass-through ──────────────────────────────

    [Fact]
    public void TrySetUnixMode_LinuxAppliesMode()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var path = Path.Combine(_tempRoot, "script.sh");
        File.WriteAllText(path, "#!/bin/sh\necho hi");

        var mgr = new UnixFilePermissionManager();
        // 0750 — pre-Phase-12 LocalScriptService:774 mode for executable scripts
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                   UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        mgr.TrySetUnixMode(path, mode);

        File.GetUnixFileMode(path).ShouldBe(mode);
    }

    [Fact]
    public void TrySetUnixMode_WindowsIsNoOp()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_tempRoot, "script.ps1");
        File.WriteAllText(path, "Write-Host hi");

        // On Windows, mode bits don't translate. The call must NOT throw
        // and must NOT touch ACLs — those are RestrictToOwner's job.
        var mgr = new WindowsFilePermissionManager();
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Should.NotThrow(() => mgr.TrySetUnixMode(path, mode));
    }

    [Fact]
    public void TrySetUnixMode_NonexistentPath_DoesNotThrow()
    {
        // Defensive — same justification as RestrictToOwner.
        var mgr = FilePermissionManagerFactory.Resolve();

        Should.NotThrow(() => mgr.TrySetUnixMode(
            Path.Combine(_tempRoot, "ghost.txt"),
            UnixFileMode.UserRead));
    }

    // ── Round-trip via existing ResilientFileSystem call sites ────────────

    [Fact]
    public void UnixManager_RoundTrip_LinuxPreservesPreviousBehaviour()
    {
        // Pin: the pre-Phase-12 LocalScriptService modes still apply
        // identically through the new interface on Linux.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var path = Path.Combine(_tempRoot, "asset.json");
        File.WriteAllText(path, "{}");

        var mgr = new UnixFilePermissionManager();
        // 0640 — LocalScriptService:851 mode for non-executable workspace files
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        mgr.TrySetUnixMode(path, mode);

        File.GetUnixFileMode(path).ShouldBe(mode);
    }
}
