using Squid.Core.Services.Machines.Upgrade.Methods;

namespace Squid.UnitTests.Services.Machines.Upgrade.Methods;

public sealed class AptUpgradeMethodTests
{
    private static readonly AptUpgradeMethod Method = new();

    [Fact]
    public void Name_IsLowercaseStableIdentifier()
    {
        Method.Name.ShouldBe("apt");
    }

    [Fact]
    public void RequiresExplicitSwap_IsFalse_BecauseDpkgWritesFilesDirectly()
    {
        // dpkg's transactional write IS the swap — Phase B's mv-swap block
        // would error trying to mv a non-existent $EXTRACT for apt installs.
        Method.RequiresExplicitSwap.ShouldBeFalse();
    }

    [Fact]
    public void Render_ProbesAptGetAndSquidSourcesFile()
    {
        // Both probes must be present: apt-get binary present AND OUR repo
        // configured. Without the sources.list check, `apt-get install
        // squid-tentacle` could find a colliding package from another repo.
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.ShouldContain("command -v apt-get");
        snippet.ShouldContain("/etc/apt/sources.list.d/squid.list");
    }

    [Fact]
    public void Render_PinsExactTargetVersion()
    {
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.ShouldContain("squid-tentacle=1.4.0");
    }

    [Fact]
    public void Render_GatesOnInstallOk_ToShortCircuitWhenHigherPriorityMethodAlreadyRan()
    {
        // The contract every method honours — first line gates on $INSTALL_OK
        // so the script's apt → yum → tarball priority order works.
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.TrimStart().ShouldStartWith("if [ \"$INSTALL_OK\" != \"1\" ]");
    }

    [Fact]
    public void Render_SetsBothInstallOkAndInstallMethod_OnSuccess()
    {
        // Phase B branches on INSTALL_METHOD; if a method sets INSTALL_OK
        // but forgets INSTALL_METHOD, Phase B can't decide whether to swap.
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.ShouldContain("INSTALL_OK=1");
        snippet.ShouldContain("INSTALL_METHOD=apt");
    }

    [Fact]
    public void Render_RecordsOldVersionForManualRollback()
    {
        // Status file consumes OLD_VERSION_APT to print a copy-paste-ready
        // downgrade command in the ROLLBACK_NEEDED detail.
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.ShouldContain("OLD_VERSION_APT=$(dpkg-query");
    }

    [Fact]
    public void Render_AllowsDowngrades_BecauseUpgradeRequestMaySpecifyOlderVersion()
    {
        // Operator-driven downgrades are a legitimate scenario (revert).
        // apt errors by default when downgrading; --allow-downgrades flag
        // is required to make `pkg=oldver` work.
        Method.RenderDetectAndInstall("1.4.0").ShouldContain("--allow-downgrades");
    }

    [Fact]
    public void Render_LogsSkipReason_WhenPrerequisitesAbsent()
    {
        // Operator running `journalctl -u squid-tentacle` or reading the
        // status file's detail field needs to see WHY apt was skipped, not
        // just observe that yum/tarball was tried instead.
        Method.RenderDetectAndInstall("1.4.0")
            .ShouldContain("[upgrade-method:apt] Skipped: apt-get not found OR /etc/apt/sources.list.d/squid.list missing");
    }
}
