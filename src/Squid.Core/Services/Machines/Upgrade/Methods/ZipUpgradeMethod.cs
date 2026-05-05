namespace Squid.Core.Services.Machines.Upgrade.Methods;

/// <summary>
/// Windows analog of <see cref="TarballUpgradeMethod"/>.
/// Install via direct GitHub Releases <c>.zip</c> download. Always available
/// (no host prerequisites beyond <c>Invoke-WebRequest</c> + <c>Expand-Archive</c>,
/// both built into Windows PowerShell 5.0+) and serves as the universal
/// fallback when neither Chocolatey nor MSI is configured. Same path the
///  PowerShell template will take when no higher-priority
/// method's detection block matches.
/// </summary>
/// <remarks>
/// <para><b>Two reasons this method is the last in the chain</b> (mirrors
/// the Linux tarball rationale exactly):
/// <list type="number">
///   <item>Chocolatey / MSI are visible to <c>choco list --local-only</c> /
///         <c>Get-Package</c> / Add/Remove Programs; zip installs are
///         invisible to those tools, so the operator's standard inventory
///         tooling can't see Squid Tentacle as a managed package.</item>
///   <item>Future Chocolatey / MSI methods can ship
///         downgrade paths declaratively (<c>choco install --version=…</c>
///         / MSI <c>UPGRADECODE</c> Windows Installer logic), whereas zip
///         operators have to manually re-run install-tentacle.ps1 against
///         the older release.</item>
/// </list></para>
///
/// <para><b>RequiresExplicitSwap = true</b>: the PowerShell template owns
/// the <c>Invoke-WebRequest</c> + SHA256 verify + <c>Expand-Archive</c> +
/// <c>Move-Item</c>-swap logic for this method. The
/// <see cref="RenderDetectAndInstall"/> method here just emits the
/// branch-marker that triggers the existing in-template zip block —
/// keeping the ~300 lines of PowerShell out of C# string-builders, same
/// discipline as the Linux tarball method.</para>
/// </remarks>
public sealed class ZipUpgradeMethod : IWindowsUpgradeMethod
{
    public string Name => "zip";

    public bool RequiresExplicitSwap => true;

    public string RenderDetectAndInstall(string targetVersion)
    {
        // Zip is the unconditional fallback — no detection needed beyond
        // "have we already installed via a higher-priority method?". The
        // actual download/verify/extract logic stays in the PowerShell
        // template. This snippet just
        // signals the template that zip is the chosen method, which gates
        // the existing zip block.
        //
        // PowerShell idiom: `if ($INSTALL_OK -ne $true)` is the analog of
        // bash's `if [ "$INSTALL_OK" != "1" ]`. Variable name kept the same
        // (INSTALL_OK / INSTALL_METHOD) so operators reading both Linux + Windows
        // upgrade logs see identical decision tags.
        return $$"""
                 if ($INSTALL_OK -ne $true) {
                   Write-Host "[upgrade-method:zip] No package manager method matched — using GitHub Releases zip as fallback"
                   $INSTALL_METHOD = 'zip'
                   # NOTE: actual download + extract is performed by the
                   # zip block further down in the template; $INSTALL_OK
                   # is set there, not here. This marker exists so the log
                   # makes the decision visible to operators.
                 }
                 """;
    }
}
