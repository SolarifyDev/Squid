namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Windows-side counterpart to <see cref="ILinuxUpgradeMethod"/>.
/// One way to put the new tentacle binary on a Windows host during the in-UI
/// upgrade flow. Implementations render a self-contained PowerShell snippet
/// that detects whether the method is usable on the agent host and, if so,
/// performs the install. The snippets are concatenated by the
/// <c>WindowsTentacleUpgradeStrategy</c> (not yet shipped) in priority order;
/// the first one whose detection block matches sets <c>$INSTALL_OK = $true</c>,
/// and the remaining snippets short-circuit out.
/// </summary>
/// <remarks>
/// <para>This abstraction matches the same pattern used on Linux — apt-get
/// → yum → tarball — except the Windows method order will be: chocolatey
/// (if registered) → MSI (if WiX MSI is published per) → zip
/// (universal fallback).  ships only the zip method; later
/// phases add MSI and chocolatey behind operator-driven feature gates.</para>
///
/// <para><b>Snippet contract every implementation must honour:</b>
/// <list type="bullet">
///   <item>Top of snippet: short-circuit if <c>$INSTALL_OK -eq $true</c>
///         (a prior method already installed). PowerShell uses
///         <c>$variable</c> not bash <c>$VAR</c>; the variable name itself
///         (<c>INSTALL_OK</c>) deliberately matches the Linux convention so
///         operators reading both sets of logs see the same vocabulary.</item>
///   <item>Probe: detect whether the host satisfies the method's
///         prerequisites (Windows feature, command-on-path, registered
///         package source). If not, do nothing and return.</item>
///   <item>On match: perform the install, then set
///         <c>$INSTALL_OK = $true</c> and <c>$INSTALL_METHOD = '&lt;name&gt;'</c>
///         as PowerShell vars so the post-scope phase knows which
///         restart/rollback semantics to apply.</item>
///   <item>Log every decision branch with a tag like
///         <c>[upgrade-method:&lt;name&gt;]</c> so operators can grep
///         <c>Get-EventLog -LogName Application</c> (or the
///         <c>upgrade.log</c> file under <c>%ProgramData%\Squid\Tentacle\upgrade</c>
///         per 's <see cref="WindowsUpgradeStatusStorage"/>) for
///         the path taken.</item>
///   <item>If the install attempt fails (non-zero <c>$LASTEXITCODE</c> /
///         exception), do NOT set <c>$INSTALL_OK</c> — the next method in
///         the chain will be tried.</item>
/// </list></para>
///
/// <para><b>Why PowerShell snippets, not C# logic that calls into the agent:</b>
/// the detection (chocolatey installed, registry-resident MSI, etc.) only
/// makes sense on the agent host. Rendering a single self-contained
/// PowerShell script that the existing Halibut RPC can execute is far
/// simpler than a multi-round "probe capabilities → server picks method →
/// server sends second script" flow, and matches the architecture the
/// Linux side settled on. Same architectural choice, different shell.</para>
///
/// <para><b>Linux↔Windows contract symmetry</b>: the public surface of
/// <see cref="IWindowsUpgradeMethod"/> is intentionally identical to
/// <see cref="ILinuxUpgradeMethod"/> — same three members
/// (<see cref="Name"/>, <see cref="RenderDetectAndInstall"/>,
/// <see cref="RequiresExplicitSwap"/>) with the same XML-doc contract.
/// Renaming or reshaping one but not the other would break the operator's
/// mental model + the strategy's ability to dispatch
/// uniformly across both shell flavours.</para>
/// </remarks>
public interface IWindowsUpgradeMethod
{
    /// <summary>
    /// Stable identifier used in logs, tests, and the
    /// <c>$INSTALL_METHOD</c> PowerShell variable. Lowercase, alphanumeric,
    /// no spaces — appears in <c>[upgrade-method:&lt;name&gt;]</c> log tags.
    /// Mirrors <see cref="ILinuxUpgradeMethod.Name"/>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Generates the PowerShell snippet that, on the agent host, detects
    /// whether this method is usable and (if so) installs <paramref name="targetVersion"/>.
    ///
    /// <para>Must be valid PowerShell (5.1 minimum — Windows PowerShell on
    /// Server 2016+; ALSO works on PowerShell Core / 7+). Must respect the
    /// <c>$INSTALL_OK</c> / <c>$INSTALL_METHOD</c> contract documented on
    /// <see cref="IWindowsUpgradeMethod"/>.</para>
    /// </summary>
    string RenderDetectAndInstall(string targetVersion);

    /// <summary>
    /// Whether this method writes the new binary atomically from a staging
    /// directory we control (<see langword="true"/>; needs explicit
    /// <c>Move-Item .\bak / Move-Item .\staging</c> in the scope phase) or
    /// whether the package manager handles it directly (<see langword="false"/>;
    /// the in-scope swap step is a no-op for this method).
    /// </summary>
    /// <remarks>
    /// <para>Zip method: <see langword="true"/> — we
    /// <c>Expand-Archive</c> to <c>%TEMP%\squid-tentacle-staging</c> and
    /// need the scope phase to <c>Move-Item</c> it into
    /// <c>%ProgramFiles%\Squid Tentacle\</c>.</para>
    /// <para>MSI method (future): <see langword="false"/> —
    /// <c>msiexec /i …</c> already wrote files to the install location
    /// during the install step; the scope phase only needs to restart the
    /// service.</para>
    /// </remarks>
    bool RequiresExplicitSwap { get; }
}
