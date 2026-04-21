namespace Squid.Core.Services.Machines.Upgrade.Methods;

/// <summary>
/// Install via the RHEL/CentOS/Fedora YUM/DNF repo at
/// <c>https://squid.solarifyai.com/rpm</c>. Activated when the agent host
/// has <c>dnf</c> or <c>yum</c> AND the Squid repo descriptor at
/// <c>/etc/yum.repos.d/squid-tentacle.repo</c> (placed by
/// <c>install-tentacle.sh</c> on RPM-based fresh installs).
/// </summary>
/// <remarks>
/// <para><b>Version pinning:</b> RPM uses NVR (Name-Version-Release)
/// format. We pin to the Version+Release-1 form
/// (e.g. <c>squid-tentacle-1.4.0-1</c>) — the <c>-1</c> is the packaging
/// release iteration that <c>publish-linux-packages.yml</c> hard-codes.
/// If the version isn't in the repo, dnf errors and we fall through.</para>
///
/// <para><b>dnf vs yum:</b> Modern RHEL 8+/Rocky/Fedora ship dnf as the
/// default; older RHEL 7 has yum. dnf is a drop-in replacement so both
/// invocations work, but we prefer dnf when present (faster transactions,
/// better error messages).</para>
/// </remarks>
public sealed class YumUpgradeMethod : ILinuxUpgradeMethod
{
    /// <summary>
    /// RPM packaging release iteration — must match the <c>-1</c> in
    /// <c>publish-linux-packages.yml</c>'s
    /// <c>--package "squid-tentacle-${V}-1.${arch}.rpm"</c>. If we ever rev
    /// that, this constant moves with it.
    /// <para><b>Pinned by</b>
    /// <c>YumUpgradeMethodTests.PackagingRelease_PinnedToWorkflowContract</c>
    /// — the test asserts this value matches the literal in the workflow
    /// YAML so a drift between producer (CI) and consumer (upgrade script)
    /// can't pass CI silently and cause every yum upgrade to fall through
    /// to tarball.</para>
    /// </summary>
    public const string PackagingRelease = "1";

    public string Name => "yum";

    public bool RequiresExplicitSwap => false;

    public string RenderDetectAndInstall(string targetVersion)
    {

        return $$"""
                 if [ "$INSTALL_OK" != "1" ]; then
                   YUM_BIN=""
                   if   command -v dnf >/dev/null 2>&1; then YUM_BIN=dnf
                   elif command -v yum >/dev/null 2>&1; then YUM_BIN=yum
                   fi
                   if [ -n "$YUM_BIN" ] && [ -f /etc/yum.repos.d/squid-tentacle.repo ]; then
                     echo "[upgrade-method:yum] Squid RPM repo configured — attempting \`$YUM_BIN install squid-tentacle-{{targetVersion}}-{{PackagingRelease}}\`"
                     OLD_VERSION_RPM=$(rpm -q squid-tentacle --qf '%{VERSION}-%{RELEASE}' 2>/dev/null || echo "<none>")
                     echo "[upgrade-method:yum] Pre-upgrade version: $OLD_VERSION_RPM"
                     if sudo $YUM_BIN install -y "squid-tentacle-{{targetVersion}}-{{PackagingRelease}}"; then
                       INSTALL_OK=1
                       INSTALL_METHOD=yum
                       echo "[upgrade-method:yum] Installed squid-tentacle-{{targetVersion}}-{{PackagingRelease}} via $YUM_BIN"
                     else
                       echo "[upgrade-method:yum] $YUM_BIN install failed (exit $?); falling through to next method"
                     fi
                   else
                     echo "[upgrade-method:yum] Skipped: dnf/yum not found OR /etc/yum.repos.d/squid-tentacle.repo missing"
                   fi
                 fi
                 """;
    }
}
