using System.Runtime.Versioning;
using System.Security.Principal;

namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.A.3 — Windows impl.
///
/// <para><b>Default-user contract on Windows</b>: empty string means
/// "use platform default" — sc.exe with no <c>obj=</c> arg installs
/// the service running as LocalSystem. Operators who need a different
/// identity supply <c>--username</c> + <c>--password</c> at install
/// time (future Phase C work; the LSA <c>SeServiceLogonRight</c> grant
/// + WMI <c>Win32_Service.Change</c> dance is what Octopus does).</para>
///
/// <para><b>Ownership semantics</b>: Windows doesn't have Unix chown.
/// Files are governed by ACLs, which
/// <see cref="IFilePermissionManager.RestrictToOwner"/> already handles
/// (Phase-12.A.1). <see cref="TrySetOwnership"/> here is a no-op that
/// returns true so the same shared call site (e.g.
/// <c>InstanceOwnershipHandover</c>) can run on both platforms without
/// adding an explicit OS branch.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceUserProvider : IServiceUserProvider
{
    /// <summary>
    /// Empty = "use platform default" = LocalSystem on Windows.
    /// Operator can override via future <c>service install --username FOO</c>.
    /// </summary>
    public string DefaultServiceUser => string.Empty;

    public bool IsRunningElevated()
    {
        // Windows admin check via WindowsPrincipal. The check is itself
        // Windows-only — caller should never invoke this from Linux/macOS
        // because the platform attribute gates compilation, but defensively
        // wrap in OS check anyway.
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public bool ServiceUserExists(string user)
    {
        // Empty user = "platform default" = LocalSystem → always exists.
        // For now we don't verify other named users; Phase C's
        // service-install command will add a LookupAccountName-based
        // pre-flight if/when operators supply --username FOO.
        if (string.IsNullOrEmpty(user)) return true;

        // Conservative: assume named users exist; if they don't, sc.exe
        // install will fail with a clear error at install time.
        return true;
    }

    /// <summary>
    /// No-op on Windows — ownership-style hardening goes through
    /// <see cref="IFilePermissionManager.RestrictToOwner"/>, not here.
    /// Returns true so the cross-platform caller treats it as success
    /// without spamming a "failed" log.
    /// </summary>
    public bool TrySetOwnership(string path, string user)
    {
        _ = path;
        _ = user;
        return true;
    }
}
