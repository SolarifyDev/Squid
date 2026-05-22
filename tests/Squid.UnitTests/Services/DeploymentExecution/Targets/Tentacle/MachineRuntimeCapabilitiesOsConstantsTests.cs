using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// pins the agent↔server cross-process OS-string contract.
/// The Tentacle agent's <c>RuntimeCapabilitiesInspector.DetectOs()</c> writes
/// these strings into <c>CapabilitiesResponse.Metadata["os"]</c>; the server
/// reads them via <c>MachineRuntimeCapabilities.Os</c> and routes through
/// the <c>IsWindows / IsLinux / IsMacOS / IsUnknown</c> property predicates.
/// A rename of any literal silently breaks the routing across BOTH halves.
/// Pinning every constant value here (mirrored by the agent-side test in
/// <c>Squid.Tentacle.Tests</c>) makes the rename a build-time-visible
/// decision per Rule 8.
/// </summary>
public sealed class MachineRuntimeCapabilitiesOsConstantsTests
{
    // ── Constant value pinning (Rule 8) ─────────────────────────────────────
    //
    // The string values are the WIRE CONTRACT between agent and server. A
    // rename here without updating RuntimeCapabilitiesInspector silently
    // breaks every Windows/macOS tentacle's routing — the resolver would
    // fall through to "no strategy registered" because the capability cache
    // would carry the OLD string while the predicates check the NEW one.

    [Fact]
    public void AgentOperatingSystems_Windows_PinnedToCanonicalString()
        => AgentOperatingSystems.Windows.ShouldBe("Windows");

    [Fact]
    public void AgentOperatingSystems_Linux_PinnedToCanonicalString()
        => AgentOperatingSystems.Linux.ShouldBe("Linux");

    [Fact]
    public void AgentOperatingSystems_MacOS_PinnedToCanonicalString()
        => AgentOperatingSystems.MacOS.ShouldBe("macOS");

    [Fact]
    public void AgentOperatingSystems_Unknown_PinnedToCanonicalString()
        => AgentOperatingSystems.Unknown.ShouldBe("Unknown");

    // ── IsWindows property predicates ───────────────────────────────────────

    [Theory]
    [InlineData("Windows", true)]   // exact agent-reported string
    [InlineData("windows", true)]   // case-insensitive
    [InlineData("WINDOWS", true)]
    [InlineData("WiNdOwS", true)]
    // Legacy long-form Windows strings — older Tentacle binaries (and any
    // out-of-band caller that wrote Environment.OSVersion.VersionString into
    // the cache directly) emit these. The operator-reported failure mode
    // ("CommunicationStyle 'TentaclePolling' is not supported for in-UI
    // upgrades") happened because strict equality treated these as a foreign
    // OS, so BOTH the Windows AND Linux upgrade strategies rejected. Fix:
    // delegate to WindowsOsStringHelper.IsWindows() — the same shared helper
    // that already powers the IIS dispatch guard + MachineCapabilitySet
    // projection (PR #348). Single source of truth for "is this a Windows
    // host?" across the entire codebase.
    [InlineData("Microsoft Windows NT 10.0.19045.0", true)]    // Win10 22H2 — operator's specific failure mode
    [InlineData("Microsoft Windows NT 10.0.22631.0", true)]    // Win11 23H2
    [InlineData("Microsoft Windows NT 10.0.17763.0", true)]    // Server 2019
    [InlineData("Microsoft Windows NT 10.0.20348.0", true)]    // Server 2022
    [InlineData("Microsoft Windows NT 6.3.9600.0", true)]      // Server 2012 R2 / Win 8.1
    [InlineData("Microsoft Windows Server 2022 Datacenter", true)]    // friendly long form
    [InlineData("microsoft windows nt 10.0.19045.0", true)]    // case-insensitive long form
    [InlineData("Linux", false)]
    [InlineData("macOS", false)]
    [InlineData("Unknown", false)]
    [InlineData("", false)]
    [InlineData("FreeBSD", false)]
    [InlineData("Win", false)]                       // partial-match must NOT match — guards against future agent rename to abbreviated form
    [InlineData("WindowsSomethingElse", false)]      // anti-false-positive anchor — doesn't start with "Microsoft Windows"
    [InlineData("LinuxOnWindowsSubsystem", false)]   // contains "Windows" but not at start → must reject
    public void IsWindows_MatchesCanonicalString_CaseInsensitive(string os, bool expected)
    {
        var caps = new MachineRuntimeCapabilities { Os = os };

        caps.IsWindows.ShouldBe(expected);
    }

    // ── IsLinux property predicates ─────────────────────────────────────────

    [Theory]
    [InlineData("Linux", true)]
    [InlineData("linux", true)]
    [InlineData("LINUX", true)]
    [InlineData("Windows", false)]
    [InlineData("macOS", false)]
    [InlineData("Unknown", false)]
    [InlineData("", false)]
    // Drift detector: long-form Windows must NEVER be claimed by IsLinux. Without
    // this row, a future refactor that accidentally widened IsLinux (e.g.
    // "if not strictly canonical, fall through to Linux") would silently route
    // a Windows agent to the Linux upgrade strategy → tarball MD5 mismatch /
    // /var/lib path missing / cryptic operator error. Single-owner invariant
    // in MachineUpgradeService.ResolveStrategy depends on this exclusion.
    [InlineData("Microsoft Windows NT 10.0.19045.0", false)]
    [InlineData("Microsoft Windows Server 2022 Datacenter", false)]
    public void IsLinux_MatchesCanonicalString_CaseInsensitive(string os, bool expected)
    {
        var caps = new MachineRuntimeCapabilities { Os = os };

        caps.IsLinux.ShouldBe(expected);
    }

    // ── IsMacOS property predicates ─────────────────────────────────────────

    [Theory]
    [InlineData("macOS", true)]
    [InlineData("MACOS", true)]
    [InlineData("macos", true)]
    [InlineData("MacOS", true)]
    [InlineData("Windows", false)]
    [InlineData("Linux", false)]
    [InlineData("", false)]
    public void IsMacOS_MatchesCanonicalString_CaseInsensitive(string os, bool expected)
    {
        var caps = new MachineRuntimeCapabilities { Os = os };

        caps.IsMacOS.ShouldBe(expected);
    }

    // ── IsUnknown property predicates ───────────────────────────────────────
    //
    // IsUnknown covers TWO cases:
    //   (1) Cold cache — Os is empty/whitespace (agent never health-checked).
    //   (2) Explicit fallback — agent's RuntimeCapabilitiesInspector hit the
    //       "neither Windows nor macOS nor Linux" branch and reported the
    //       literal "Unknown" sentinel.
    // Linux strategy claims this case (`IsLinux || IsUnknown`) as historical
    // default — there was no OS axis and Linux was the only
    // strategy.

    [Theory]
    [InlineData("", true)]              // cold cache
    [InlineData("   ", true)]           // whitespace-only (defensive)
    [InlineData("Unknown", true)]       // explicit agent-side fallback
    [InlineData("UNKNOWN", true)]       // case-insensitive
    [InlineData("unknown", true)]
    [InlineData("Linux", false)]        // explicit Linux is NOT unknown
    [InlineData("Windows", false)]      // explicit Windows is NOT unknown
    [InlineData("macOS", false)]
    [InlineData("FreeBSD", false)]      // unrecognised but explicit OS is NOT unknown — gives "no strategy registered" instead of falling to Linux
    // Drift detector: long-form Windows is a RECOGNISED Windows variant, not
    // Unknown. Without this row, the Linux strategy's `IsLinux || IsUnknown`
    // claim path would accidentally route long-form Windows agents to the
    // Linux upgrade strategy (because IsLinux=false, IsUnknown=true historically
    // when only canonical "Windows" was recognised). The combo of (IsWindows
    // tolerant) + (IsUnknown strict) is the invariant that keeps single-owner
    // routing correct.
    [InlineData("Microsoft Windows NT 10.0.19045.0", false)]
    [InlineData("Microsoft Windows Server 2022 Datacenter", false)]
    public void IsUnknown_CoversBothColdCacheAndExplicitUnknownFallback(string os, bool expected)
    {
        var caps = new MachineRuntimeCapabilities { Os = os };

        caps.IsUnknown.ShouldBe(expected);
    }

    [Fact]
    public void Empty_IsUnknown_True_ColdCacheHistoricalDefault()
    {
        // The static Empty sentinel used by every cold-cache call site
        // (MachineUpgradeService, TentacleEndpointVariableContributor, etc.)
        // MUST report IsUnknown=true so the Linux historical-default path
        // claims it. Without this, every cold-cache machine would fall
        // through to "no strategy registered" until the first health check.
        MachineRuntimeCapabilities.Empty.IsUnknown.ShouldBeTrue();
        MachineRuntimeCapabilities.Empty.IsLinux.ShouldBeFalse();
        MachineRuntimeCapabilities.Empty.IsWindows.ShouldBeFalse();
        MachineRuntimeCapabilities.Empty.IsMacOS.ShouldBeFalse();
    }

    [Fact]
    public void OsPredicates_AreMutuallyExclusive_ExceptForUnknownOverlap()
    {
        // For any non-Unknown OS, exactly one of IsWindows / IsLinux /
        // IsMacOS is true. IsUnknown is true ONLY for empty/whitespace/
        // "Unknown" — a real OS name doesn't overlap.
        var windows = new MachineRuntimeCapabilities { Os = AgentOperatingSystems.Windows };
        windows.IsWindows.ShouldBeTrue();
        windows.IsLinux.ShouldBeFalse();
        windows.IsMacOS.ShouldBeFalse();
        windows.IsUnknown.ShouldBeFalse();

        var linux = new MachineRuntimeCapabilities { Os = AgentOperatingSystems.Linux };
        linux.IsWindows.ShouldBeFalse();
        linux.IsLinux.ShouldBeTrue();
        linux.IsMacOS.ShouldBeFalse();
        linux.IsUnknown.ShouldBeFalse();

        var mac = new MachineRuntimeCapabilities { Os = AgentOperatingSystems.MacOS };
        mac.IsWindows.ShouldBeFalse();
        mac.IsLinux.ShouldBeFalse();
        mac.IsMacOS.ShouldBeTrue();
        mac.IsUnknown.ShouldBeFalse();
    }

    [Fact]
    public void OsPredicates_LegacyMicrosoftWindowsForm_RoutesToWindowsOnly()
    {
        // The operator-reported failure mode: agent metadata carries the legacy
        // "Microsoft Windows NT ..." string from Environment.OSVersion.VersionString.
        // After the fix, all FOUR predicates must agree with the canonical
        // "Windows" case — IsWindows=true, IsLinux=false, IsMacOS=false,
        // IsUnknown=false. Without this, MachineUpgradeService.ResolveStrategy
        // sees zero claimants and emits "CommunicationStyle 'TentaclePolling'
        // is not supported for in-UI upgrades" even though the agent IS Windows.
        var longForm = new MachineRuntimeCapabilities { Os = "Microsoft Windows NT 10.0.19045.0" };

        longForm.IsWindows.ShouldBeTrue(
            customMessage: "Legacy 'Microsoft Windows NT ...' MUST be recognised as Windows so WindowsTentacleUpgradeStrategy claims it");
        longForm.IsLinux.ShouldBeFalse(
            customMessage: "Long-form Windows must NOT trigger IsLinux — would silently route to Linux strategy");
        longForm.IsMacOS.ShouldBeFalse();
        longForm.IsUnknown.ShouldBeFalse(
            customMessage: "Long-form Windows is a recognised Windows variant, not Unknown — Linux's `IsLinux || IsUnknown` claim path must NOT activate");
    }
}
