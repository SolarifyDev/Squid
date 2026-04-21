namespace Squid.Core.Services.Machines.Upgrade.Methods;

/// <summary>
/// Install via direct GitHub Releases tarball download. Always available
/// (no host prerequisites beyond <c>curl</c> + <c>tar</c>) and serves as
/// the universal fallback when neither apt nor yum is configured. Same
/// path the script took before Phase 2 Part 2 — the existing download +
/// extract + atomic-swap logic in the bash template handles this method.
/// </summary>
/// <remarks>
/// <para><b>Two reasons this method is the last in the chain</b>:
/// <list type="number">
///   <item>apt/yum are faster on package-manager hosts (no full tarball
///         re-download if some files are unchanged, deps resolved
///         declaratively, easier downgrade path).</item>
///   <item>apt/yum-installed packages are visible to <c>dpkg -l</c> /
///         <c>rpm -qa</c>, so ops tools see Squid Tentacle as a managed
///         package; tarball installs are invisible to those tools.</item>
/// </list></para>
///
/// <para><b>RequiresExplicitSwap = true:</b> the bash template still
/// owns the curl + tar + verify + mv-swap logic for this method. The
/// <see cref="RenderDetectAndInstall"/> method here just emits the
/// branch-marker that triggers the existing in-template tarball block.</para>
/// </remarks>
public sealed class TarballUpgradeMethod : ILinuxUpgradeMethod
{
    public string Name => "tarball";

    public bool RequiresExplicitSwap => true;

    public string RenderDetectAndInstall(string targetVersion)
    {
        // Tarball is the unconditional fallback — no detection needed
        // beyond "have we already installed via a higher-priority method?".
        // The actual download/verify/extract logic stays in the bash
        // template (we don't duplicate ~80 lines of curl/tar/sha here).
        // This snippet just signals the template that tarball is the
        // chosen method, which gates the existing tarball block.
        return $$"""
                 if [ "$INSTALL_OK" != "1" ]; then
                   echo "[upgrade-method:tarball] No package manager method matched — using GitHub Releases tarball as fallback"
                   INSTALL_METHOD=tarball
                   # NOTE: actual download + extract is performed by the
                   # tarball block further down in the template; INSTALL_OK
                   # is set there, not here. This marker exists so the log
                   # makes the decision visible to operators.
                 fi
                 """;
    }
}
