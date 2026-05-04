using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// P1-Phase12.E.3 — coverage for the Windows tentacle upgrade strategy
/// stub. Phase 12.E.3 ships routing only (Os-aware <c>CanHandle</c>);
/// the actual Halibut dispatch + PowerShell template render is Phase
/// 12.E.4's deliverable. Until then, <c>UpgradeAsync</c> returns
/// <see cref="MachineUpgradeStatus.NotSupported"/> with a clear roadmap-
/// aligned remediation hint.
///
/// <para>The stub matters for operator UX: with the Phase 12.E.3 OS-aware
/// resolver in place, a Windows agent's upgrade attempt would otherwise
/// fall through to "no strategy registered for style 'TentaclePolling'" —
/// confusing because the operator wouldn't know whether Windows is
/// fundamentally unsupported or just unfinished. The stub draws the line:
/// Windows is recognised, dispatch arrives in 12.E.4.</para>
/// </summary>
public sealed class WindowsTentacleUpgradeStrategyTests
{
    private readonly WindowsTentacleUpgradeStrategy _strategy = new();

    // ── CanHandle: routes by (style, OS) ────────────────────────────────────

    [Theory]
    [InlineData("TentaclePolling", true)]
    [InlineData("TentacleListening", true)]
    [InlineData("KubernetesAgent", false)]
    [InlineData("KubernetesApi", false)]
    [InlineData("Ssh", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CanHandle_OnlyMatchesTentacleStyles_OnWindows(string style, bool expected)
    {
        // The Windows strategy only claims TentaclePolling / TentacleListening
        // (the Halibut wire-protocol styles). Same style filter as the Linux
        // strategy — but combined with the Windows OS filter below, the (style,
        // OS) tuple is unique and the resolver's "exactly one owner"
        // invariant holds.
        var windowsCaps = new MachineRuntimeCapabilities { Os = "Windows" };

        _strategy.CanHandle(style, windowsCaps).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Windows", true)]
    [InlineData("windows", true)]    // case-insensitive
    [InlineData("WINDOWS", true)]
    [InlineData("Linux", false)]     // Linux strategy claims this
    [InlineData("macOS", false)]     // Linux strategy claims everything non-Windows
    [InlineData("", false)]          // cold cache → Linux strategy claims as historical default
    public void CanHandle_RoutesByOs_OnlyClaimsWindowsAgents(string os, bool expected)
    {
        // P1-Phase12.E.3 — Windows strategy ONLY claims agents that have
        // explicitly self-identified as Windows via their last health-check
        // capabilities probe. Cold cache (empty Os) falls to Linux strategy
        // because that's the historical default — preserves pre-Phase-12
        // behaviour for the overwhelming majority of existing operator
        // deployments. A real Windows tentacle would have reported its OS
        // before any operator-driven upgrade in practice (the FE only shows
        // upgrade affordance for healthy targets).
        var capabilities = new MachineRuntimeCapabilities { Os = os };

        _strategy.CanHandle(nameof(CommunicationStyle.TentaclePolling), capabilities).ShouldBe(expected);
    }

    [Fact]
    public void CanHandle_NullCapabilities_TreatedAsEmpty_DoesNotClaimWindows()
    {
        // Defensive: a future caller passing null capabilities (instead of
        // MachineRuntimeCapabilities.Empty) should not NPE. Same fallback
        // as the empty-OS case — Windows strategy does NOT claim, so the
        // resolver routes to Linux as the historical default.
        _strategy.CanHandle(nameof(CommunicationStyle.TentaclePolling), capabilities: null).ShouldBeFalse();
    }

    // ── UpgradeAsync: Phase 12.E.3 stub returns NotSupported ────────────────

    [Fact]
    public async Task UpgradeAsync_ReturnsNotSupportedWithRoadmapHint()
    {
        // Phase 12.E.3 placeholder: routing wired, dispatch deferred to E.4.
        // Operator sees a clear roadmap-aligned message instead of a generic
        // "not registered" error. The hint must mention 12.E.4 (the next
        // step on the roadmap) and the manual install-tentacle.ps1 fallback
        // so operators have a "what do I do today" answer in the meantime.
        var result = await _strategy.UpgradeAsync(
            new Machine { Id = 1, Name = "win-agent" },
            "1.6.0",
            CancellationToken.None);

        result.Status.ShouldBe(MachineUpgradeStatus.NotSupported);
        result.Detail.ShouldContain("12.E.4");
        result.Detail.ShouldContain("install-tentacle.ps1");
        result.Detail.ShouldContain("1.6.0");

        // Audit N-6 / Phase-11 contract: stub did nothing → cache stays valid,
        // server doesn't need to invalidate it on the next health check.
        result.AgentVersionMayHaveChanged.ShouldBeFalse(
            "stub returns NotSupported without doing anything → cache stays valid");
    }

    [Fact]
    public async Task UpgradeAsync_NullTargetVersion_StillReturnsNotSupportedWithoutNullRef()
    {
        // Defensive: even if the orchestrator somehow passes a null target
        // version (it shouldn't — MachineUpgradeService validates with
        // SemVer.TryParse before dispatch), the stub must not throw NRE.
        // The hint should still be operator-actionable.
        var result = await _strategy.UpgradeAsync(
            new Machine { Id = 1, Name = "win-agent" },
            targetVersion: null,
            CancellationToken.None);

        result.Status.ShouldBe(MachineUpgradeStatus.NotSupported);
        result.Detail.ShouldContain("12.E.4");
        result.AgentVersionMayHaveChanged.ShouldBeFalse();
    }
}
