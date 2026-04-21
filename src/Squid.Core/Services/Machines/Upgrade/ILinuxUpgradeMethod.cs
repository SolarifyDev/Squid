namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// One way to put the new tentacle binary on disk during the in-UI upgrade
/// flow. Implementations render a self-contained bash snippet that detects
/// whether the method is usable on the agent host and, if so, performs the
/// install. The snippets are concatenated by
/// <see cref="LinuxTentacleUpgradeStrategy"/> in priority order; the first
/// one whose detection block matches sets <c>INSTALL_OK=1</c>, and the
/// remaining snippets short-circuit out.
/// </summary>
/// <remarks>
/// <para>This abstraction matches Octopus's documented order — apt-get →
/// yum → tarball — and lets us add new methods (snap, helm-on-host, etc.)
/// later without touching <see cref="LinuxTentacleUpgradeStrategy"/>.</para>
///
/// <para><b>Snippet contract every implementation must honour:</b>
/// <list type="bullet">
///   <item>Top of snippet: short-circuit if <c>$INSTALL_OK = 1</c> (a prior
///         method already installed).</item>
///   <item>Probe: detect whether the host satisfies the method's
///         prerequisites (binary on PATH, repo configured, etc.). If not,
///         do nothing and return.</item>
///   <item>On match: perform the install, then set <c>INSTALL_OK=1</c> and
///         <c>INSTALL_METHOD=&lt;name&gt;</c> as shell vars so the post-scope
///         phase knows which restart/rollback semantics to apply.</item>
///   <item>Log every decision branch with a tag like
///         <c>[upgrade-method:&lt;name&gt;]</c> so operators can grep
///         <c>journalctl -u squid-tentacle</c> for the path taken.</item>
///   <item>If the install attempt fails (exit non-zero), do NOT set
///         INSTALL_OK — the next method in the chain will be tried.</item>
/// </list></para>
///
/// <para><b>Why bash snippets, not C# logic that calls into the agent:</b>
/// the detection (apt-cache policy, systemd version, etc.) only makes sense
/// on the agent host. Rendering a single self-contained bash script that
/// the existing Halibut RPC can execute is far simpler than a multi-round
/// "probe capabilities → server picks method → server sends second script"
/// flow, and matches the architecture Octopus settled on.</para>
/// </remarks>
public interface ILinuxUpgradeMethod
{
    /// <summary>
    /// Stable identifier used in logs, tests, and the
    /// <c>INSTALL_METHOD</c> bash variable. Lowercase, alphanumeric, no
    /// spaces — appears in <c>[upgrade-method:&lt;name&gt;]</c> log tags.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Generates the bash snippet that, on the agent host, detects whether
    /// this method is usable and (if so) installs <paramref name="targetVersion"/>.
    ///
    /// <para>Must be valid bash under <c>set -euo pipefail</c>. Must respect
    /// the <c>INSTALL_OK</c>/<c>INSTALL_METHOD</c> contract documented on
    /// <see cref="ILinuxUpgradeMethod"/>.</para>
    /// </summary>
    string RenderDetectAndInstall(string targetVersion);

    /// <summary>
    /// Whether this method writes the new binary atomically from a staging
    /// directory we control (true; needs explicit <c>mv .bak / mv staging</c>
    /// in the scope phase) or whether the OS package manager handles it
    /// directly (false; the in-scope swap step is a no-op for this method).
    /// </summary>
    /// <remarks>
    /// Tarball: true — we extract to <c>/tmp/.../extract</c> and need the
    /// scope phase to mv it into <c>/opt/squid-tentacle</c>.
    /// apt/yum: false — the package manager already wrote files to
    /// <c>/opt/squid-tentacle</c> during the install step; the scope phase
    /// only needs to restart the service.
    /// </remarks>
    bool RequiresExplicitSwap { get; }
}
