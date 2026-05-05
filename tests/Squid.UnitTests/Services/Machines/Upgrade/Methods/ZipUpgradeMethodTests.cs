using System.Linq;
using Squid.Core.Services.Machines.Upgrade.Methods;

namespace Squid.UnitTests.Services.Machines.Upgrade.Methods;

/// <summary>
/// pins the <see cref="ZipUpgradeMethod"/> snippet contract.
/// Mirrors <see cref="TarballUpgradeMethodTests"/> shape exactly: the zip
/// method is the universal Windows fallback (always available — no host
/// prerequisites beyond <c>Invoke-WebRequest</c> + <c>Expand-Archive</c>),
/// same role tarball plays on Linux.
/// </summary>
public sealed class ZipUpgradeMethodTests
{
    private static readonly ZipUpgradeMethod Method = new();

    [Fact]
    public void Name_IsLowercaseStableIdentifier()
    {
        // Lowercase, alphanumeric, no spaces — appears in
        // [upgrade-method:zip] log tags. Pinned per the IWindowsUpgradeMethod
        // contract documented on the interface XML doc.
        Method.Name.ShouldBe("zip");
    }

    [Fact]
    public void RequiresExplicitSwap_IsTrue_BecausePhaseBOwnsTheMove()
    {
        // Zip method extracts to %TEMP%\squid-tentacle-staging; Phase B's
        // Move-Item swap block places the new binary at
        // %ProgramFiles%\Squid Tentacle\. Future MSI / Chocolatey methods
        // skip Phase B's swap because their package managers wrote those
        // files transactionally — same dichotomy as Linux tarball vs apt/yum.
        Method.RequiresExplicitSwap.ShouldBeTrue();
    }

    [Fact]
    public void Render_OnlySetsInstallMethod_NotInstallOk()
    {
        // Zip is a marker — the actual download/extract/swap logic stays
        // in the PowerShell template (~300 lines we don't want in C#
        // string-builders). The marker just signals the template that zip
        // is the chosen path; the template's zip block is what flips
        // $INSTALL_OK = $true after Invoke-WebRequest + Expand-Archive
        // + Move-Item all succeed.
        //
        // Pin the EXACT PowerShell assignment ($INSTALL_METHOD = 'zip')
        // not just the substring — without the literal-assignment match a
        // commented-out `# $INSTALL_METHOD = 'zip'` would silently pass the
        // weaker substring check, breaking the contract on the agent.
        var snippet = Method.RenderDetectAndInstall("1.6.0");

        // Stricter than ShouldContain: at least one LINE (not commented-
        // out — PowerShell uses `#` for comments) must, when trimmed, equal
        // the literal assignment. Defends against a future "commented out
        // for debugging" change silently disabling the marker.
        var hasUncommentedAssignment = snippet
            .Split('\n')
            .Any(line =>
            {
                var trimmed = line.TrimStart();
                return !trimmed.StartsWith("#") && trimmed.TrimEnd().Equals("$INSTALL_METHOD = 'zip'", StringComparison.Ordinal);
            });

        hasUncommentedAssignment.ShouldBeTrue(
            "snippet must emit an uncommented PowerShell assignment `$INSTALL_METHOD = 'zip'` — the template's zip block branches on this exact value, and a commented-out form would silently make the marker non-functional");

        snippet.ShouldNotContain("$INSTALL_OK = $true",
            customMessage: "zip marker must NOT set $INSTALL_OK — that's the template's zip block's job after download + verify + extract + swap");
    }

    [Fact]
    public void Render_GatesOnInstallOk()
    {
        // Same contract as the other methods — short-circuit if a
        // higher-priority method already succeeded. PowerShell uses
        // `if ($INSTALL_OK -ne $true)` as the equivalent of bash's
        // `if [ "$INSTALL_OK" != "1" ]`.
        var snippet = Method.RenderDetectAndInstall("1.6.0").TrimStart();

        snippet.ShouldStartWith("if (", Case.Sensitive);
        snippet.ShouldContain("$INSTALL_OK", Case.Sensitive);
    }

    [Fact]
    public void Render_LogsItIsTheFallback()
    {
        // Operator clarity — the zip log message says explicitly that it's
        // the fallback (not "we chose zip because it's faster" or similar).
        // Helps ops decide whether to install Chocolatey / pre-stage MSI on
        // their Windows hosts. Mirrors the Linux tarball log line shape.
        var snippet = Method.RenderDetectAndInstall("1.6.0");

        snippet.ShouldContain("[upgrade-method:zip]", Case.Sensitive);
        snippet.ShouldContain("fallback", Case.Insensitive);
    }
}
