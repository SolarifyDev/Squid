using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Serilog;

namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.A.1 — Windows implementation of
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

        // 3. Grant FullControl to the current user (the agent's process
        //    identity). Anyone else gets nothing.
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("WindowsIdentity.GetCurrent().User is null — cannot determine owner SID");

        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            AccessControlType.Allow));

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

        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            // Inherit the rule onto contained files + subdirs so anything
            // dropped into the workspace later is also owner-only.
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        dirInfo.SetAccessControl(security);
    }
}
