using Squid.Core.Services.Machines.Upgrade.Methods;

namespace Squid.UnitTests.Services.Machines.Upgrade.Methods;

public sealed class YumUpgradeMethodTests
{
    private static readonly YumUpgradeMethod Method = new();

    [Fact]
    public void Name_IsLowercaseStableIdentifier()
    {
        Method.Name.ShouldBe("yum");
    }

    [Fact]
    public void RequiresExplicitSwap_IsFalse_BecauseRpmWritesFilesDirectly()
    {
        Method.RequiresExplicitSwap.ShouldBeFalse();
    }

    [Fact]
    public void Render_PrefersDnf_FallsBackToYum()
    {
        // Modern RHEL 8+ / Fedora / Rocky default to dnf; older RHEL 7 still
        // has yum. dnf is a drop-in replacement so both invocations work,
        // but dnf gets first-pick (faster transactions, better errors).
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        var dnfIdx = snippet.IndexOf("YUM_BIN=dnf", StringComparison.Ordinal);
        var yumIdx = snippet.IndexOf("YUM_BIN=yum", StringComparison.Ordinal);

        dnfIdx.ShouldBeGreaterThan(-1, "dnf branch must be present");
        yumIdx.ShouldBeGreaterThan(-1, "yum fallback must be present");
        dnfIdx.ShouldBeLessThan(yumIdx, "dnf must be probed before yum (preferred on modern distros)");
    }

    [Fact]
    public void Render_ProbesYumRepoFile_Not_JustBinary()
    {
        // The squid-tentacle.repo file presence guarantees the package will
        // resolve to OUR repo, not a colliding name from EPEL or similar.
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.ShouldContain("/etc/yum.repos.d/squid-tentacle.repo");
    }

    [Fact]
    public void Render_PinsExactNVR_NameVersionRelease()
    {
        // RPM's NVR format. The "-1" packaging release is hardcoded to
        // match publish-linux-packages.yml's `--package "squid-tentacle-${V}-1.${arch}.rpm"`.
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.ShouldContain("squid-tentacle-1.4.0-1");
    }

    [Fact]
    public void Render_GatesOnInstallOk()
    {
        Method.RenderDetectAndInstall("1.4.0").TrimStart()
            .ShouldStartWith("if [ \"$INSTALL_OK\" != \"1\" ]");
    }

    [Fact]
    public void Render_SetsInstallMethodToYum_OnSuccess()
    {
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.ShouldContain("INSTALL_METHOD=yum");
    }

    [Fact]
    public void Render_RecordsOldVersionForManualRollback()
    {
        Method.RenderDetectAndInstall("1.4.0").ShouldContain("OLD_VERSION_RPM=$(rpm -q squid-tentacle");
    }
}
