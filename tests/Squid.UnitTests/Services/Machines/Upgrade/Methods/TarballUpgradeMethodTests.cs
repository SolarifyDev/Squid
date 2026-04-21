using Squid.Core.Services.Machines.Upgrade.Methods;

namespace Squid.UnitTests.Services.Machines.Upgrade.Methods;

public sealed class TarballUpgradeMethodTests
{
    private static readonly TarballUpgradeMethod Method = new();

    [Fact]
    public void Name_IsLowercaseStableIdentifier()
    {
        Method.Name.ShouldBe("tarball");
    }

    [Fact]
    public void RequiresExplicitSwap_IsTrue_BecausePhaseBOwnsTheMv()
    {
        // Tarball method extracts to /tmp/.../extract; Phase B's mv-swap
        // block is what places the new binary at /opt/squid-tentacle.
        // The other methods (apt/yum) skip Phase B's swap because dpkg/rpm
        // already wrote those files transactionally.
        Method.RequiresExplicitSwap.ShouldBeTrue();
    }

    [Fact]
    public void Render_OnlySetsInstallMethod_NotInstallOk()
    {
        // Tarball is a marker — the actual download/extract logic stays in
        // the bash template (~80 lines we don't want in C# string-builders).
        // The marker just signals the template that tarball is the chosen
        // path; the template's tarball block is what flips INSTALL_OK=1.
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.ShouldContain("INSTALL_METHOD=tarball");
        snippet.ShouldNotContain("INSTALL_OK=1",
            customMessage: "tarball marker must NOT set INSTALL_OK — that's the template's tarball block's job after curl + verify + extract");
    }

    [Fact]
    public void Render_GatesOnInstallOk()
    {
        // Same contract as the other methods — short-circuit if a
        // higher-priority method already succeeded.
        Method.RenderDetectAndInstall("1.4.0").TrimStart()
            .ShouldStartWith("if [ \"$INSTALL_OK\" != \"1\" ]");
    }

    [Fact]
    public void Render_LogsItIsTheFallback()
    {
        // Operator clarity — the tarball log message says explicitly that
        // it's the fallback (not "we chose tarball because it's faster" or
        // similar). Helps ops decide whether to configure apt/yum repos.
        Method.RenderDetectAndInstall("1.4.0")
            .ShouldContain("[upgrade-method:tarball] No package manager method matched — using GitHub Releases tarball as fallback");
    }
}
