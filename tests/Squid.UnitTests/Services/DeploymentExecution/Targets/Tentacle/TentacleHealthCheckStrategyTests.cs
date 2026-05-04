using System.Linq;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public class TentacleHealthCheckStrategyTests
{
    private readonly Mock<IHalibutClientFactory> _clientFactory = new();

    [Fact]
    public async Task CheckHealth_ListeningEndpoint_ReturnsHealthy()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentacleListening","Uri":"https://10.0.0.5:10933/","Thumbprint":"AABB"}""");
        var capsClient = SetupCapabilitiesClient("2.1.0", "IScriptService");

        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object);
        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Detail.ShouldContain("2.1.0");
        result.Detail.ShouldContain("IScriptService");
    }

    [Fact]
    public async Task CheckHealth_PollingEndpoint_ReturnsHealthy()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""");
        var capsClient = SetupCapabilitiesClient("2.0.0", "IScriptService");

        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object);
        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Detail.ShouldContain("2.0.0");
    }

    [Fact]
    public async Task CheckHealth_MissingThumbprint_ReturnsUnhealthy()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentacleListening","Uri":"https://10.0.0.5:10933/"}""");

        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object);
        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("missing");
    }

    [Fact]
    public async Task CheckHealth_NullCapabilitiesResponse_ReturnsUnhealthy()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""");
        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync((CapabilitiesResponse)null);

        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>()))
            .Returns(capsClient.Object);

        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object);
        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("null");
    }

    [Fact]
    public async Task CheckHealth_HalibutThrows_ReturnsUnhealthy()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""");
        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ThrowsAsync(new HalibutClientException("Connection refused"));

        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>()))
            .Returns(capsClient.Object);

        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object);
        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("Connection refused");
    }

    [Fact]
    public async Task CheckHealth_EmptyEndpoint_ReturnsUnhealthy()
    {
        var machine = MachineWithEndpoint("");

        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object);
        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
    }

    // ========== Phase 3: capabilities metadata → machine runtime cache ==========

    [Fact]
    public async Task CheckHealth_PopulatesRuntimeCapabilitiesCache_FromMetadata()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""", machineId: 77);

        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = "3.2.0",
                SupportedServices = new List<string> { "IScriptService/v1" },
                Metadata = new Dictionary<string, string>
                {
                    ["os"] = "Linux",
                    ["defaultShell"] = "bash",
                    ["installedShells"] = "bash,pwsh"
                }
            });
        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>())).Returns(capsClient.Object);

        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object, cache);

        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Detail.ShouldContain("OS=Linux");
        result.Detail.ShouldContain("shell=bash");

        var cached = cache.TryGet(77);
        cached.Os.ShouldBe("Linux");
        cached.DefaultShell.ShouldBe("bash");
        cached.AgentVersion.ShouldBe("3.2.0");
    }

    [Fact]
    public async Task CheckHealth_NoMetadata_StillHealthy_AndCacheStoresAgentVersion()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""", machineId: 88);

        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = "1.0.0",
                SupportedServices = new List<string> { "IScriptService/v1" },
                Metadata = new Dictionary<string, string>()
            });
        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>())).Returns(capsClient.Object);

        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object, cache);

        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Detail.ShouldNotContain("OS=");

        var cached = cache.TryGet(88);
        cached.AgentVersion.ShouldBe("1.0.0");
        cached.Os.ShouldBeEmpty();
    }

    // ========================================================================
    // P1-Phase12.E.8.3 — UpgradeStatusPayload snapshot cache wiring.
    //
    // The health-check parses the agent's upgradeStatus metadata into a
    // typed payload + dispatches it to TWO independent consumers: the stale-
    // lock reconciler AND the IUpgradeEventTimelineStore status cache. The
    // cache is what backs the FE-facing GetUpgradeStatus endpoint so
    // operators see the agent's structured ExitCode without SSHing.
    //
    // The pin: when the agent reports an upgradeStatus key with valid JSON,
    // the cache MUST receive the payload (with ExitCode preserved) on every
    // probe. Tests use the real InMemoryUpgradeEventTimelineStore so the
    // cache contract is exercised end-to-end (vs. mocking the store, which
    // would only verify the call shape).
    // ========================================================================

    [Fact]
    public async Task CheckHealth_AgentReportsUpgradeStatus_PopulatesTimelineStoreCache_IncludingExitCode()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""", machineId: 100);

        var statusJson = """
            {
              "schemaVersion": 2,
              "status": "FAILED",
              "targetVersion": "1.6.0",
              "installMethod": "zip",
              "exitCode": 7,
              "detail": "SHA256 mismatch (expected ABC, got DEF)",
              "startedAt": "2026-05-04T10:00:00Z",
              "scriptPid": 12345
            }
            """;

        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = "1.6.0",
                SupportedServices = new List<string> { "IScriptService/v1" },
                Metadata = new Dictionary<string, string>
                {
                    ["upgradeStatus"] = statusJson
                }
            });
        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>())).Returns(capsClient.Object);

        var timelineStore = new InMemoryUpgradeEventTimelineStore();
        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object, capabilitiesCache: null, upgradeLockReconciler: null, upgradeEventStore: timelineStore);

        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();

        var cached = timelineStore.GetStatus(100);
        cached.ShouldNotBeNull(
            customMessage: "agent reported upgradeStatus key → cache MUST be populated. Without this, the GetUpgradeStatus API returns null and operators can't see the structured failure mode.");
        cached.Status.ShouldBe("FAILED");
        cached.ExitCode.ShouldBe(7,
            customMessage: "ExitCode round-trips agent → CapabilitiesService.Metadata → server parser → timeline cache. This pins the load-bearing field added in 12.E.7.B-2.");
        cached.Detail.ShouldContain("SHA256 mismatch");
        cached.TargetVersion.ShouldBe("1.6.0");
        cached.InstallMethod.ShouldBe("zip");
    }

    [Fact]
    public async Task CheckHealth_AgentOmitsUpgradeStatusKey_CachePreservesPriorEntry_NoOverwrite()
    {
        // Operator did upgrade A (failed), then no further activity. Subsequent
        // health checks have no upgradeStatus key (agent's last-upgrade.json
        // wasn't touched). The cache must PRESERVE the prior cached payload
        // so the operator can still see the failure outcome — clearing on
        // every "no key" probe would erase historical data within seconds
        // of the upgrade ending.
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""", machineId: 101);

        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = "1.6.0",
                SupportedServices = new List<string> { "IScriptService/v1" },
                Metadata = new Dictionary<string, string>()  // NO upgradeStatus key
            });
        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>())).Returns(capsClient.Object);

        var timelineStore = new InMemoryUpgradeEventTimelineStore();
        // Pre-seed a prior failure outcome (simulates a previous probe that
        // populated the cache before the agent's status file aged out).
        timelineStore.StoreStatus(101, new UpgradeStatusPayload
        {
            SchemaVersion = 2,
            Status = "FAILED",
            ExitCode = 7,
            Detail = "old failure"
        });
        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object, capabilitiesCache: null, upgradeLockReconciler: null, upgradeEventStore: timelineStore);

        await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        var cached = timelineStore.GetStatus(101);
        cached.ShouldNotBeNull(
            customMessage: "absent upgradeStatus key MUST NOT clear the cache — operator's view of historical outcomes depends on the cache surviving 'quiet' health checks");
        cached.ExitCode.ShouldBe(7);
    }

    [Fact]
    public async Task CheckHealth_NullTimelineStore_CheckHealthStillWorks_NoNullRef()
    {
        // The IUpgradeEventTimelineStore is an OPTIONAL dependency (CapabilitiesCache
        // pattern from 12.A: nullable ctor arg). A health-check strategy
        // wired in tests / scenarios without the store MUST NOT NPE on the
        // status-cache code path.
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""", machineId: 102);

        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = "1.6.0",
                SupportedServices = new List<string> { "IScriptService/v1" },
                Metadata = new Dictionary<string, string>
                {
                    ["upgradeStatus"] = """{"schemaVersion":2,"status":"SUCCESS","exitCode":0}"""
                }
            });
        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>())).Returns(capsClient.Object);

        // No timeline store wired.
        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object);

        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue(
            customMessage: "null upgrade event store MUST NOT propagate as a health-check failure — the optional dep is genuinely optional");
    }

    [Fact]
    public async Task CheckHealth_AgentReportsMalformedStatusJson_CachePreservesPriorEntry()
    {
        // Defensive: a partial-write or disk-full at the agent could produce
        // truncated JSON. UpgradeStatusPayload.TryParse returns null on parse
        // failure → the cache MUST NOT replace a known-good prior entry with
        // null. Mirrors the existing reconciler discipline.
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""", machineId: 103);

        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = "1.6.0",
                SupportedServices = new List<string> { "IScriptService/v1" },
                Metadata = new Dictionary<string, string>
                {
                    ["upgradeStatus"] = """{"schemaVersion":"""    // truncated — TryParse returns null
                }
            });
        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>())).Returns(capsClient.Object);

        var timelineStore = new InMemoryUpgradeEventTimelineStore();
        timelineStore.StoreStatus(103, new UpgradeStatusPayload
        {
            SchemaVersion = 2,
            Status = "SUCCESS",
            ExitCode = 0,
            Detail = "previous good outcome"
        });
        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object, capabilitiesCache: null, upgradeLockReconciler: null, upgradeEventStore: timelineStore);

        await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        var cached = timelineStore.GetStatus(103);
        cached.ShouldNotBeNull(
            customMessage: "TryParse-null on malformed JSON MUST NOT overwrite the cached prior payload — operator's view of last-known-good would otherwise be silently nuked by a transient write race");
        cached.Detail.ShouldBe("previous good outcome");
    }

    private Mock<IAsyncCapabilitiesService> SetupCapabilitiesClient(string agentVersion, params string[] services)
    {
        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = agentVersion,
                SupportedServices = services.ToList()
            });

        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>()))
            .Returns(capsClient.Object);

        return capsClient;
    }

    private static Machine MachineWithEndpoint(string endpointJson, int machineId = 1)
        => new() { Id = machineId, Name = "test-machine", Endpoint = endpointJson };
}
