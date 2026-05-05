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
    [InlineData("Linux", false)]
    [InlineData("macOS", false)]
    [InlineData("Unknown", false)]
    [InlineData("", false)]
    [InlineData("FreeBSD", false)]
    [InlineData("Win", false)]      // partial-match must NOT match — guards against future agent rename to abbreviated form
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
}
