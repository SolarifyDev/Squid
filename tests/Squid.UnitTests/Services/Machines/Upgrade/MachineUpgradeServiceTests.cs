using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Machines.Exceptions;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;

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
    private readonly Mock<IBundledTentacleVersionProvider> _versionProvider = new();
    private readonly Mock<IMachineUpgradeStrategy> _linuxStrategy = new();
    private readonly Mock<IMachineUpgradeStrategy> _k8sStrategy = new();
    private readonly MachineUpgradeService _service;

    public MachineUpgradeServiceTests()
    {
        _versionProvider.Setup(x => x.GetBundledVersion()).Returns("1.4.0");

        _linuxStrategy.Setup(s => s.CanHandle(nameof(CommunicationStyle.TentaclePolling))).Returns(true);
        _linuxStrategy.Setup(s => s.CanHandle(nameof(CommunicationStyle.TentacleListening))).Returns(true);

        _k8sStrategy.Setup(s => s.CanHandle(nameof(CommunicationStyle.KubernetesAgent))).Returns(true);

        _service = new MachineUpgradeService(
            _machineDataProvider.Object,
            _runtimeCache,
            _versionProvider.Object,
            new[] { _linuxStrategy.Object, _k8sStrategy.Object });
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
    public async Task UpgradeAsync_NoBundleAndNoExplicitTarget_ReturnsFailedWithGuidance()
    {
        _versionProvider.Setup(x => x.GetBundledVersion()).Returns(string.Empty);
        ArrangeMachine(id: 1, style: nameof(CommunicationStyle.TentaclePolling));

        var resp = await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 1 }, CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.Failed);
        resp.Detail.ShouldContain("No target version");
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
    public async Task UpgradeAsync_CurrentVersionNewerThanTarget_ShortCircuitsAlreadyUpToDate()
    {
        ArrangeMachine(id: 8, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(8, new Dictionary<string, string>(), agentVersion: "2.0.0");

        var resp = await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 8 }, CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.AlreadyUpToDate);
    }

    [Fact]
    public async Task UpgradeAsync_OperatorOverridesTargetVersion_PreferredOverBundle()
    {
        ArrangeMachine(id: 11, style: nameof(CommunicationStyle.TentaclePolling));
        _runtimeCache.Store(11, new Dictionary<string, string>(), agentVersion: "1.0.0");

        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), "9.9.9", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.Upgraded, Detail = "ok" });

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
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.Upgraded, Detail = "done" });

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
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.NotSupported, Detail = "phase 2" });

        var resp = await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 22 }, CancellationToken.None);

        resp.Status.ShouldBe(MachineUpgradeStatus.NotSupported);
        _linuxStrategy.Verify(s => s.UpgradeAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
    public async Task UpgradeAsync_CacheMiss_ProceedsWithEmptyCurrentVersion()
    {
        // Cold cache — a brand-new machine that hasn't been health-checked yet.
        // We can't pre-skip, but we still dispatch so the operator's request
        // does something useful. Strategy returns Initiated; service surfaces it.
        ArrangeMachine(id: 44, style: nameof(CommunicationStyle.TentaclePolling));
        _linuxStrategy
            .Setup(s => s.UpgradeAsync(It.IsAny<Machine>(), "1.4.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineUpgradeOutcome { Status = MachineUpgradeStatus.Initiated, Detail = "dispatched" });

        var resp = await _service.UpgradeAsync(new UpgradeMachineCommand { MachineId = 44 }, CancellationToken.None);

        resp.CurrentVersion.ShouldBe(string.Empty);
        resp.Status.ShouldBe(MachineUpgradeStatus.Initiated);
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
