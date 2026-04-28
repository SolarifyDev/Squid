namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.A.1 (Windows Tentacle foundations) — cross-platform file
/// permission abstraction.
///
/// <para><b>Why this exists</b>: pre-Phase-12 the agent had 6+ scattered
/// call sites of <c>File.SetUnixFileMode(path, mode)</c>, each gated by
/// <c>OperatingSystem.IsWindows()</c> with a silent skip. As Windows
/// Tentacle support comes online, the secret-bearing paths (PFX,
/// encrypted config, sensitive variables) need genuine ACL hardening
/// on Windows — not a no-op skip. Other paths (workspace scripts,
/// asset files) tolerate Windows' default inherited ACL.</para>
///
/// <para>Two-method surface keeps the abstraction minimal:
/// <list type="bullet">
///   <item><see cref="RestrictToOwner"/> — security-bearing path: Linux
///         0600 (file) / 0700 (dir); Windows ACL break-inheritance +
///         Owner FullControl. Use for secrets.</item>
///   <item><see cref="TrySetUnixMode"/> — legacy pass-through: applies
///         mode bits on Unix, no-op on Windows. Use where the mode is
///         tradition + style, not hardening.</item>
/// </list></para>
///
/// <para>Resolved via <see cref="FilePermissionManagerFactory"/> at the
/// call site so the static implementations stay platform-independent.</para>
/// </summary>
public interface IFilePermissionManager
{
    /// <summary>
    /// Restrict the file or directory at <paramref name="path"/> to
    /// owner-only access. Used for secret-bearing paths (PFX with cert
    /// private key, encrypted-config files, sensitiveVariables.json
    /// during script execution).
    ///
    /// <para>Best-effort — non-existent paths and unsupported FS / OS
    /// errors do NOT throw. Permission tightening is hardening, not
    /// correctness; a failure here logs at Debug and continues.</para>
    /// </summary>
    /// <param name="path">Filesystem path. May not exist (no-op).</param>
    /// <param name="isDirectory">When true, target is a directory and
    ///     gets the executable bit on Unix (0700) so the owner can
    ///     traverse. When false, target is a file (0600).</param>
    void RestrictToOwner(string path, bool isDirectory = false);

    /// <summary>
    /// Apply Unix-style mode bits if running on a Unix platform.
    /// No-op on Windows (Windows uses ACLs not mode bits; mode-bit
    /// translation is lossy and Windows files are typically governed
    /// by parent-directory inherited ACLs anyway).
    ///
    /// <para>Use this for the workspace scripts / asset files that
    /// pre-Phase-12 called <c>File.SetUnixFileMode</c> directly with
    /// modes like 0640 / 0750 — those modes carry semantic intent on
    /// Unix but Windows tolerates the parent-dir's inherited ACL just
    /// fine.</para>
    /// </summary>
    void TrySetUnixMode(string path, UnixFileMode mode);
}
