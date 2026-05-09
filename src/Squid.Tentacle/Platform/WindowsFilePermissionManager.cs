using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Serilog;

namespace Squid.Tentacle.Platform;

/// <summary>
/// Windows implementation of
/// <see cref="IFilePermissionManager"/>.
///
/// <para><b>Mapping</b>:
/// <list type="bullet">
///   <item><see cref="RestrictToOwner"/> — break ACL inheritance, remove
///         all existing rules, add a single FullControl rule for the
///         current user. Windows analog of Unix 0600 / 0700.</item>
///   <item><see cref="TrySetUnixMode"/> — no-op. Windows ACLs cannot
///         meaningfully express Unix mode bits (no separate "owner /
///         group / other" axis), and Windows files are conventionally
///         governed by the parent-directory's inherited ACL anyway.</item>
/// </list></para>
///
/// <para><b>Why break inheritance for RestrictToOwner</b>: typical Windows
/// install paths (<c>%PROGRAMDATA%\Squid\...</c>) inherit
/// <c>BUILTIN\Users:Read</c> from the parent. For a PFX containing a cert
/// private key, that's a privesc vector — any local user could read the
/// key. Breaking inheritance + adding only Owner FullControl matches
/// what an operator running <c>icacls .\secret.pfx /inheritance:r /grant
/// %USERNAME%:F /remove BUILTIN\Users</c> would do manually.</para>
///
/// <para>Best-effort: Windows file ACLs throw on a wide range of
/// transient errors (file disappeared, AV grabbed the handle, ACL system
/// disabled on the volume). All caught + logged at Debug — security
/// tightening is hardening, not correctness.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFilePermissionManager : IFilePermissionManager
{
    public void RestrictToOwner(string path, bool isDirectory = false)
    {
        try
        {
            if (isDirectory)
            {
                if (!Directory.Exists(path)) return;
                ApplyOwnerOnlyAcl_Directory(path);
            }
            else
            {
                if (!File.Exists(path)) return;
                ApplyOwnerOnlyAcl_File(path);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[FilePermissionManager] Windows RestrictToOwner({Path}, isDirectory={IsDir}) failed",
                path, isDirectory);
        }
    }

    public void TrySetUnixMode(string path, UnixFileMode mode)
    {
        // Intentional no-op on Windows. Mode bits don't translate cleanly
        // to ACLs; Windows files inherit from parent-dir which is the
        // conventional security boundary on this platform.
        _ = path;
        _ = mode;
    }

    /// <summary>
    /// Well-known LocalSystem SID. <c>S-1-5-18</c> is
    /// <c>SECURITY_LOCAL_SYSTEM_RID</c> — the identity SCM uses to launch
    /// services without an explicit account.
    /// </summary>
    private static readonly SecurityIdentifier LocalSystemSid =
        new(WellKnownSidType.LocalSystemSid, domainSid: null);

    /// <summary>
    /// Well-known BUILTIN\Administrators SID. <c>S-1-5-32-544</c>.
    /// Administrators can already read these files via SeBackupPrivilege;
    /// explicit grant makes recovery / debugging work without elevation
    /// rituals.
    /// </summary>
    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null);

    private static void ApplyOwnerOnlyAcl_File(string path)
    {
        var fileInfo = new FileInfo(path);
        var security = fileInfo.GetAccessControl();

        // 1. Break inheritance and remove inherited rules entirely (false =
        //    do not preserve copies of inherited rules as explicit rules).
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // 2. Strip every explicit rule that was already on the file.
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true, includeInherited: false, typeof(NTAccount)))
        {
            security.RemoveAccessRule(rule);
        }

        // 3. Grant FullControl to:
        //    a) the current user (the user who ran `register` — typically
        //       an Administrator running `Squid.Tentacle.exe register ...`)
        //    b) NT AUTHORITY\SYSTEM (LocalSystem — SCM's default service
        //       identity; without this grant, `sc start squid-tentacle`
        //       launches the binary which can't read its own config →
        //       UnauthorizedAccessException → service fails to start)
        //    c) BUILTIN\Administrators — operators debugging / recovering
        //       can elevate cmd and read the file without juggling
        //       SeBackupPrivilege ACL escapes
        //
        // Anyone NOT in (a)/(b)/(c) — including BUILTIN\Users — gets
        // nothing. This preserves the original security goal (no privesc
        // for sibling local users) while functionally enabling the
        // documented operator workflow `register` → `service install` →
        // `sc start`. Caught by PR #283 round-2 CI: pre-fix the
        // SCM-launched binary hit `UnauthorizedAccessException: Access
        // to the path '<config>.json' is denied` because LocalSystem
        // wasn't on the ACL.
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("WindowsIdentity.GetCurrent().User is null — cannot determine owner SID");

        security.AddAccessRule(new FileSystemAccessRule(
            owner, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            LocalSystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));

        fileInfo.SetAccessControl(security);
    }

    private static void ApplyOwnerOnlyAcl_Directory(string path)
    {
        var dirInfo = new DirectoryInfo(path);
        var security = dirInfo.GetAccessControl();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true, includeInherited: false, typeof(NTAccount)))
        {
            security.RemoveAccessRule(rule);
        }

        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("WindowsIdentity.GetCurrent().User is null — cannot determine owner SID");

        // Same three principals as ApplyOwnerOnlyAcl_File — see that
        // method's doc-comment for the SCM-launched-binary rationale.
        // Inheritance flags propagate the rules onto contained files +
        // subdirs so anything written later inherits the same secure-
        // but-SCM-functional ACL.
        const InheritanceFlags inheritFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        security.AddAccessRule(new FileSystemAccessRule(
            owner, FileSystemRights.FullControl, inheritFlags, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            LocalSystemSid, FileSystemRights.FullControl, inheritFlags, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            AdministratorsSid, FileSystemRights.FullControl, inheritFlags, PropagationFlags.None, AccessControlType.Allow));

        dirInfo.SetAccessControl(security);
    }
}
