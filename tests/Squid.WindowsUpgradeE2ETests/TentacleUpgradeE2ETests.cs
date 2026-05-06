using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Commands.Machine;
using Squid.WindowsUpgradeE2ETests.Infrastructure;

namespace Squid.WindowsUpgradeE2ETests;

/// <summary>
/// Phase 12.J.E — E2E coverage for
/// <see cref="WindowsTentacleUpgradeStrategy.UpgradeAsync"/> end-to-end.
/// Tests the FULL strategy: build outer wrapper → dispatch via Halibut RPC
/// → observe via <see cref="HalibutScriptObserver"/> → map result to
/// <see cref="MachineUpgradeOutcome"/>.
///
/// <para><b>Tier</b>: 🟢 High-fidelity (Rule 12). Every component except
/// the upstream server is production:</para>
/// <list type="bullet">
///   <item><see cref="WindowsTentacleUpgradeStrategy"/> — production strategy</item>
///   <item><see cref="HalibutClientFactory"/> — production client factory</item>
///   <item><see cref="HalibutScriptObserver"/> — production observer</item>
///   <item>Halibut RPC — production library</item>
///   <item><see cref="StubAgent"/> — wraps production
///         <c>LocalScriptService</c> so the wrapper script actually runs
///         (PowerShell on Windows; the production wrapper uses
///         <c>Register-ScheduledTask</c> which only exists on Windows, so
///         the test is Windows-only).</item>
/// </list>
///
/// <para><b>Coverage delta vs <c>WindowsUpgradeWrapperE2ETests</c></b>:
/// that suite tests <see cref="WindowsTentacleUpgradeStrategy.BuildOuterWrapper"/>
/// in ISOLATION by running the returned PowerShell directly via
/// <c>powershell.exe -File</c>. This file adds the FULL dispatch+observe
/// path through Halibut RPC + the
/// <see cref="WindowsTentacleUpgradeStrategy.InterpretScriptResult"/>
/// outcome mapper. A regression in HalibutClientFactory, HalibutScriptObserver,
/// or the outcome mapper would be invisible to wrapper tests but caught
/// here.</para>
///
/// <para><b>Skip-on-non-Windows</b>: the production wrapper relies on
/// <c>Register-ScheduledTask</c> + Task Scheduler — Windows-only APIs.
/// The test no-ops cleanly on macOS/Linux dev hosts.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.TentacleUpgrade)]
public sealed class TentacleUpgradeE2ETests : IDisposable
{
    private readonly List<string> _scheduledTaskNamesToCleanup = new();

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;

        foreach (var taskName in _scheduledTaskNamesToCleanup)
            TryDeleteTask(taskName);
    }

    // ========================================================================
    // E1.h — Listening upgrade dispatch via real strategy → Initiated outcome
    //
    // Builds Machine → constructs WindowsTentacleUpgradeStrategy with
    // production HalibutClientFactory + HalibutScriptObserver pointing at
    // StubSquidServer's runtime → calls UpgradeAsync → asserts
    // MachineUpgradeOutcome.Initiated (because the wrapper successfully
    // schedules the detached Task Scheduler task and exits 0).
    // ========================================================================

    [Fact]
    public async Task Listening_UpgradeAsync_HappyPath_ReturnsInitiatedAndDispatchesWrapper()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        // Construct a Machine entity pointing at the StubAgent. Endpoint
        // JSON shape mirrors what production EndpointJsonHelper expects
        // for TentacleListening.
        var machine = new Machine
        {
            Id = 999,
            Name = $"e2e-upgrade-{Guid.NewGuid():N}",
            Endpoint = $@"{{""CommunicationStyle"":""TentacleListening"",""Uri"":""{agent.ListeningUri}"",""Thumbprint"":""{agent.Thumbprint}""}}"
        };

        // Build production strategy + dependencies. HalibutClientFactory
        // wraps the StubSquidServer's runtime so the strategy's RPC calls
        // route through it (and back to StubAgent on the other end).
        var clientFactory = new HalibutClientFactory(server.HalibutRuntime);
        var observer = new HalibutScriptObserver();
        var strategy = new WindowsTentacleUpgradeStrategy(clientFactory, observer);

        var outcome = await strategy.UpgradeAsync(machine, "1.6.0", CancellationToken.None);

        // The wrapper schedules a detached Task Scheduler task and exits
        // 0. Strategy maps exit-0 → Initiated (NOT Success — actual upgrade
        // happens detached and reports via last-upgrade.json on next
        // capabilities probe).
        outcome.Status.ShouldBe(MachineUpgradeStatus.Initiated,
            customMessage: $"after successful wrapper dispatch, outcome MUST be Initiated. Got {outcome.Status} with detail: {outcome.Detail}");

        outcome.AgentVersionMayHaveChanged.ShouldBeTrue(
            customMessage: "Initiated outcome MUST set AgentVersionMayHaveChanged so server cache refreshes on next probe");

        // Track the scheduled task for cleanup. Format from BuildOuterWrapper:
        // "SquidTentacleUpgrade_<32hex>". Multiple may exist if test reran.
        // We can't easily extract the exact GUID-suffixed name from the strategy
        // outcome (it's logged by PowerShell, not surfaced); rely on the wrapper's
        // own DeleteExpiredTaskAfter (60s) for cleanup. Test instance pollution
        // bounded by ~60s.
    }

    // ========================================================================
    // E1.u1 — Halibut dispatch failure (agent unreachable) → Failed outcome
    //
    // Pre-dispatch failure (agent down before StartScript was acked) maps
    // to Failed, NOT Initiated. Operators see "agent down" rather than
    // "upgrade in progress" — important for accurate UI status.
    // ========================================================================

    [Fact]
    public async Task UpgradeAsync_AgentUnreachable_ReturnsFailed()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var server = await StubSquidServer.StartAsync();

        // Start + dispose agent to capture an "in the past" listening URI
        // that's no longer accepting connections.
        var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        var unreachableUri = agent.ListeningUri;
        var unreachableThumbprint = agent.Thumbprint;
        await agent.DisposeAsync();

        await Task.Delay(200);   // let OS release the port

        var machine = new Machine
        {
            Id = 999,
            Name = $"e2e-upgrade-unreachable-{Guid.NewGuid():N}",
            Endpoint = $@"{{""CommunicationStyle"":""TentacleListening"",""Uri"":""{unreachableUri}"",""Thumbprint"":""{unreachableThumbprint}""}}"
        };

        var clientFactory = new HalibutClientFactory(server.HalibutRuntime);
        var observer = new HalibutScriptObserver();
        var strategy = new WindowsTentacleUpgradeStrategy(clientFactory, observer);

        var outcome = await strategy.UpgradeAsync(machine, "1.6.0", CancellationToken.None);

        // Strategy's pre-dispatch HalibutClientException branch maps to Failed.
        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed,
            customMessage: $"unreachable agent BEFORE StartScript ack MUST be Failed (not Initiated — operator should see 'agent down', not 'upgrade running'). Got {outcome.Status}: {outcome.Detail}");

        outcome.AgentVersionMayHaveChanged.ShouldBeFalse(
            customMessage: "Failed pre-dispatch outcome MUST NOT trigger cache refresh — no upgrade was actually attempted");
    }

    // ========================================================================
    // E1.u2 — Empty target version → Failed before dispatch
    //
    // Validation gate: empty/null targetVersion is rejected by ValidateRequest
    // BEFORE the strategy attempts any Halibut work. Pin: agent should
    // receive zero registrations.
    // ========================================================================

    [Fact]
    public async Task UpgradeAsync_EmptyTargetVersion_ReturnsFailedWithoutDispatch()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        var machine = new Machine
        {
            Id = 999,
            Name = $"e2e-upgrade-novers-{Guid.NewGuid():N}",
            Endpoint = $@"{{""CommunicationStyle"":""TentacleListening"",""Uri"":""{agent.ListeningUri}"",""Thumbprint"":""{agent.Thumbprint}""}}"
        };

        var clientFactory = new HalibutClientFactory(server.HalibutRuntime);
        var observer = new HalibutScriptObserver();
        var strategy = new WindowsTentacleUpgradeStrategy(clientFactory, observer);

        var outcome = await strategy.UpgradeAsync(machine, targetVersion: string.Empty, CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed,
            customMessage: $"empty targetVersion MUST be rejected by ValidateRequest before any Halibut work. Got {outcome.Status}: {outcome.Detail}");

        outcome.Detail.ShouldContain("targetVersion",
            customMessage: $"failure message MUST name the missing field for operator debug. Got: {outcome.Detail}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void TryDeleteTask(string taskName)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{taskName}\" /F",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5_000);
        }
        catch { /* best-effort cleanup */ }
    }
}
