namespace Squid.Core.Services.Machines.Upgrade.Methods;

/// <summary>
/// The root-owned package-install helper that <c>install-tentacle.sh</c>
/// (1.9.9+) writes on the agent host and grants to the service user through a
/// bare-path sudoers rule. <see cref="AptUpgradeMethod"/> and
/// <see cref="YumUpgradeMethod"/> call it with <c>sudo -n {path} apt|dnf|yum
/// VERSION</c> when it exists and is executable, and fall back to the legacy
/// argument-wildcard sudoers commands otherwise (installs older than 1.9.9).
/// </summary>
/// <remarks>
/// Why a helper instead of <c>apt-get install … squid-tentacle=*</c> rules:
/// sudo-rs (the default sudo on Ubuntu 26.04+) rejects any wildcard inside a
/// command argument, which made <c>visudo</c> reject the whole generated file
/// and left new installs with no upgrade privilege at all. The helper keeps the
/// sudoers rule wildcard-free and does the argument validation itself.
/// <para>Deliberately not an environment-variable override: this is a security
/// path that must match the file the installer writes byte-for-byte. Pinned
/// against the installer by
/// <c>InstallTentacleSudoersTests.PackageInstallHelperPath_MatchesInstallScript</c>.</para>
/// </remarks>
public static class LinuxPackageInstallHelper
{
    public const string FilePath = "/usr/libexec/squid-tentacle/package-install";
}
