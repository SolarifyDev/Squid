namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.A.3 (Windows Tentacle foundations) — cross-platform service-
/// user abstraction. Consolidates the pre-Phase-12 split between
/// <c>ServiceCommand.DetectServiceUser</c> (Linux <c>getent</c>) and
/// <c>InstanceOwnershipHandover</c> (Linux <c>chown</c>) onto a single
/// typed contract.
///
/// <para><b>Three concrete impls</b>:
/// <list type="bullet">
///   <item><see cref="LinuxServiceUserProvider"/> — <c>squid-tentacle</c>
///         user, getent + chown shellouts.</item>
///   <item><see cref="WindowsServiceUserProvider"/> — empty string default
///         (= LocalSystem); <c>TrySetOwnership</c> is a no-op (Windows
///         uses ACLs not Unix ownership; see
///         <see cref="IFilePermissionManager"/>).</item>
///   <item><see cref="NullServiceUserProvider"/> — fallback for macOS /
///         unsupported / test contexts.</item>
/// </list></para>
/// </summary>
public interface IServiceUserProvider
{
    /// <summary>
    /// Conventional service-user name on this platform.
    /// Linux: <c>"squid-tentacle"</c>; Windows: <c>""</c> (= LocalSystem
    /// default); other: <c>""</c>.
    /// Service host install layer interprets empty as "use platform default".
    /// </summary>
    string DefaultServiceUser { get; }

    /// <summary>
    /// True when running under elevated privileges (root on Linux,
    /// Administrator on Windows). Used by ownership-handover to gate
    /// whether the chown / ACL-restrict actually has authority.
    /// </summary>
    bool IsRunningElevated();

    /// <summary>
    /// True iff the named user exists on the host. Linux: <c>getent passwd</c>.
    /// Windows: <c>LookupAccountName</c> (or always-true for empty/LocalSystem).
    /// Null platform always false.
    /// </summary>
    bool ServiceUserExists(string user);

    /// <summary>
    /// Best-effort: transfer ownership of <paramref name="path"/> to
    /// <paramref name="user"/>. Linux: recursive <c>chown</c>. Windows:
    /// no-op (<see cref="IFilePermissionManager.RestrictToOwner"/> handles
    /// the equivalent ACL tightening). Returns <c>true</c> on success or
    /// when no-op was the right call; false only on Linux chown failure
    /// so callers can log the operator-actionable warning.
    /// </summary>
    bool TrySetOwnership(string path, string user);
}
