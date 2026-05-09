using System.Security.AccessControl;
using System.Security.Principal;
using Shouldly;
using Squid.Tentacle.Platform;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Platform;

/// <summary>
///  (Windows Tentacle foundations) — pin the file-permission
/// abstraction contract.
///
/// <para><b>Why this exists</b>:  the agent had 6+ scattered
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
            "Secret-bearing files MUST be 0600 on Unix — AtomicFileWriter set this exact mode.");
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

    // ── Windows ACL contract: LocalSystem grant + Users denied ───────────
    //
    // Pre-#283-round-2 the Windows ACL granted ONLY the current user.
    // Diagnostic harvest from the SCM E2E test
    // (TentacleWindowsScmLaunchedRealBinaryE2ETests) showed the binary
    // launched by SCM as LocalSystem hit `UnauthorizedAccessException:
    // Access to '<config>.json' is denied` because LocalSystem was NOT
    // on the ACL. Documented operator workflow `register` →
    // `service install` → `sc start` was BROKEN on Windows.
    //
    // Fix: ApplyOwnerOnlyAcl_File now grants 3 principals:
    //   - Current user (preserved)
    //   - NT AUTHORITY\SYSTEM (LocalSystem — SCM's default service identity)
    //   - BUILTIN\Administrators (operators debugging without privesc rituals)
    //
    // Crucially, BUILTIN\Users is STILL denied — the original security
    // goal (no privesc for sibling users) is preserved.
    //
    // These tests pin BOTH directions (positive = LocalSystem present,
    // negative = Users absent) to catch any future regression that
    // accidentally over-narrows OR over-widens the ACL.

    [Fact]
    public void RestrictToOwner_File_GrantsLocalSystem()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_tempRoot, "secret.config.json");
        File.WriteAllText(path, "{ \"apiKey\": \"sensitive\" }");

        new WindowsFilePermissionManager().RestrictToOwner(path, isDirectory: false);

        var fileInfo = new FileInfo(path);
        var security = fileInfo.GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));

        var localSystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null);
        var hasLocalSystem = rules
            .OfType<FileSystemAccessRule>()
            .Any(r => ((SecurityIdentifier)r.IdentityReference).Equals(localSystemSid)
                   && r.AccessControlType == AccessControlType.Allow
                   && (r.FileSystemRights & FileSystemRights.Read) != 0);

        hasLocalSystem.ShouldBeTrue(
            customMessage: "RestrictToOwner MUST grant NT AUTHORITY\\SYSTEM read access — without it, " +
                          "SCM-launched services can't read their own config files. " +
                          "Operator workflow `register` → `service install` → `sc start` would " +
                          "fail with UnauthorizedAccessException.");
    }

    [Fact]
    public void RestrictToOwner_File_DeniesBuiltInUsers()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_tempRoot, "secret.config.json");
        File.WriteAllText(path, "{ \"apiKey\": \"sensitive\" }");

        new WindowsFilePermissionManager().RestrictToOwner(path, isDirectory: false);

        var fileInfo = new FileInfo(path);
        var security = fileInfo.GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));

        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null);
        var allowsUsers = rules
            .OfType<FileSystemAccessRule>()
            .Any(r => ((SecurityIdentifier)r.IdentityReference).Equals(usersSid)
                   && r.AccessControlType == AccessControlType.Allow);

        allowsUsers.ShouldBeFalse(
            customMessage: "RestrictToOwner MUST NOT grant BUILTIN\\Users any access — that's the original " +
                          "security goal (no privesc for sibling local users). If this fails: someone added " +
                          "Users to the ACL, defeating the security hardening that motivated this manager.");
    }

    [Fact]
    public void RestrictToOwner_BreaksInheritance()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_tempRoot, "secret.config.json");
        File.WriteAllText(path, "{ \"apiKey\": \"sensitive\" }");

        new WindowsFilePermissionManager().RestrictToOwner(path, isDirectory: false);

        var fileInfo = new FileInfo(path);
        var security = fileInfo.GetAccessControl();

        security.AreAccessRulesProtected.ShouldBeTrue(
            customMessage: "RestrictToOwner MUST break inheritance from the parent directory's ACL. " +
                          "Without this, %ProgramData% inheritance brings BUILTIN\\Users:Read back via " +
                          "the parent ACL, defeating the file-level hardening.");
    }

    // ── TrySetUnixMode — legacy pass-through ──────────────────────────────

    [Fact]
    public void TrySetUnixMode_LinuxAppliesMode()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var path = Path.Combine(_tempRoot, "script.sh");
        File.WriteAllText(path, "#!/bin/sh\necho hi");

        var mgr = new UnixFilePermissionManager();
        // 0750 — LocalScriptService:774 mode for executable scripts
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
        // Pin: the LocalScriptService modes still apply
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
