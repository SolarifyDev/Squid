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

    // ========== : capabilities metadata → machine runtime cache ==========

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
    public async Task CheckHealth_InstalledRolesInMetadata_PopulatedIntoCache()
    {
        // H7 audit followup. The original H7 test for the cache-population
        // path didn't include installedRoles in the metadata fixture, so a
        // regression that broke BuildCapabilitiesFromResponse's read of the
        // installedRoles key would have gone undetected. The wire contract
        // is: agent reports comma-separated roles in metadata["installedRoles"]
        // → server reads into MachineRuntimeCapabilities.InstalledRoles →
        // MachineCapabilitySet.ProjectRoles produces role:* slots →
        // CapabilityValidator catches missing role at plan-time. If the read
        // breaks, the whole chain silently optimistic-allows.
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-99","Thumbprint":"EEFF"}""", machineId: 199);

        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = "1.8.1",
                SupportedServices = new List<string> { "IScriptService/v1" },
                Metadata = new Dictionary<string, string>
                {
                    ["os"] = "Windows",
                    ["defaultShell"] = "powershell",
                    ["installedShells"] = "powershell,cmd",
                    ["installedRoles"] = "iis,docker"
                }
            });
        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>())).Returns(capsClient.Object);

        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object, cache);

        await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        var cached = cache.TryGet(199);
        cached.InstalledRoles.ShouldBe("iis,docker",
            customMessage: "H7 wire contract: BuildCapabilitiesFromResponse MUST read metadata[\"installedRoles\"] verbatim into MachineRuntimeCapabilities.InstalledRoles. A read-side regression here silently breaks the IIS deploy plan-time validation chain.");
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
    // UpgradeStatusPayload snapshot cache wiring.
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

    // ========================================================================
    // Durable upgrade trace — when the agent reports a TERMINAL upgrade status,
    // the strategy persists the current trace snapshot (status + events + log)
    // to the DB exactly once (gated), so the outcome survives a server restart.
    // ========================================================================

    [Fact]
    public async Task CheckHealth_TerminalUpgrade_PersistsTraceOnce_WithStatusEventsAndLog()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""", machineId: 200);
        SetupCapsClientReturning(TraceResponse("SUCCESS", exitCode: 0));

        var store = new InMemoryUpgradeEventTimelineStore();
        var gate = new UpgradeTracePersistenceGate();
        var (persistence, captured) = CapturingPersistence();

        var persister = new UpgradeTracePersister(store, persistence.Object, gate);
        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object, upgradeEventStore: store, upgradeTracePersister: persister);

        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();

        persistence.Verify(p => p.SaveAsync(200, It.IsAny<UpgradeTraceSnapshot>(), It.IsAny<CancellationToken>()), Times.Once);
        captured.Count.ShouldBe(1);
        captured[0].Status.Status.ShouldBe("SUCCESS");
        captured[0].Status.ExitCode.ShouldBe(0);
        captured[0].Events.Count.ShouldBe(2, "the persisted snapshot must include the event timeline captured on this probe.");
        captured[0].Log.ShouldNotBeNullOrEmpty("the persisted snapshot must include the Phase B log captured on this probe.");

        gate.AlreadyPersisted(200, captured[0].Signature).ShouldBeTrue("a successful persist must mark the gate so the same outcome isn't re-written.");
    }

    [Theory]
    [InlineData("IN_PROGRESS")]
    [InlineData("SWAPPED")]
    [InlineData("ROLLING_BACK")]
    public async Task CheckHealth_InFlightUpgrade_DoesNotPersistTrace(string inFlightStatus)
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""", machineId: 201);
        SetupCapsClientReturning(TraceResponse(inFlightStatus));

        var store = new InMemoryUpgradeEventTimelineStore();
        var (persistence, _) = CapturingPersistence();

        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object, upgradeEventStore: store, upgradeTracePersister: new UpgradeTracePersister(store, persistence.Object, new UpgradeTracePersistenceGate()));

        await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        persistence.Verify(p => p.SaveAsync(It.IsAny<int>(), It.IsAny<UpgradeTraceSnapshot>(), It.IsAny<CancellationToken>()), Times.Never,
            "an in-flight upgrade has not concluded — persisting now would write a non-final snapshot AND re-write on every probe.");
    }

    [Fact]
    public async Task CheckHealth_SameTerminalOutcomeReReportedAcrossProbes_PersistsOnlyOnce()
    {
        // The agent keeps reporting SUCCESS on every probe until the next
        // upgrade. The gate must suppress all but the first persist — this is
        // what lets us keep the timeline cache in-memory without a per-probe
        // DB write storm.
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""", machineId: 202);
        SetupCapsClientReturning(TraceResponse("SUCCESS", exitCode: 0));

        var store = new InMemoryUpgradeEventTimelineStore();
        var gate = new UpgradeTracePersistenceGate();
        var (persistence, _) = CapturingPersistence();

        var persister = new UpgradeTracePersister(store, persistence.Object, gate);
        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object, upgradeEventStore: store, upgradeTracePersister: persister);

        await strategy.CheckHealthAsync(machine, null, CancellationToken.None);
        await strategy.CheckHealthAsync(machine, null, CancellationToken.None);
        await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        persistence.Verify(p => p.SaveAsync(202, It.IsAny<UpgradeTraceSnapshot>(), It.IsAny<CancellationToken>()), Times.Once,
            "three probes reporting the same terminal outcome must result in exactly one durable write.");
    }

    [Fact]
    public async Task CheckHealth_PersistenceThrows_HealthCheckStillHealthy_GateNotMarked()
    {
        // Best-effort: a DB hiccup must not fail the health check, and must leave
        // the gate open so the NEXT probe retries the persist.
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""", machineId: 203);
        SetupCapsClientReturning(TraceResponse("FAILED", exitCode: 7));

        var store = new InMemoryUpgradeEventTimelineStore();
        var gate = new UpgradeTracePersistenceGate();
        var persistence = new Mock<IUpgradeTracePersistence>();
        persistence.Setup(p => p.SaveAsync(It.IsAny<int>(), It.IsAny<UpgradeTraceSnapshot>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var persister = new UpgradeTracePersister(store, persistence.Object, gate);
        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object, upgradeEventStore: store, upgradeTracePersister: persister);

        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue("a durable-persist failure is advisory and must not turn a healthy tentacle unhealthy.");
        gate.AlreadyPersisted(203, "FAILED@2026-06-01T10:01:00.0000000+00:00").ShouldBeFalse(
            "a failed write must NOT mark the gate, so the next probe retries.");
    }

    [Fact]
    public async Task CheckHealth_TracePersistenceNotWired_NoThrow_StillHealthy()
    {
        // persistence + gate are OPTIONAL deps. A strategy wired without them
        // (e.g. a context that doesn't need durable traces) must not NPE.
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""", machineId: 204);
        SetupCapsClientReturning(TraceResponse("SUCCESS", exitCode: 0));

        var store = new InMemoryUpgradeEventTimelineStore();
        // No upgradeTracePersistence / upgradeTraceGate.
        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object, upgradeEventStore: store);

        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
    }

    private static (Mock<IUpgradeTracePersistence> mock, List<UpgradeTraceSnapshot> captured) CapturingPersistence()
    {
        var captured = new List<UpgradeTraceSnapshot>();
        var mock = new Mock<IUpgradeTracePersistence>();
        mock.Setup(p => p.SaveAsync(It.IsAny<int>(), It.IsAny<UpgradeTraceSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<int, UpgradeTraceSnapshot, CancellationToken>((_, s, _) => captured.Add(s))
            .Returns(Task.CompletedTask);
        return (mock, captured);
    }

    private void SetupCapsClientReturning(CapabilitiesResponse response)
    {
        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>())).ReturnsAsync(response);
        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>())).Returns(capsClient.Object);
    }

    private static CapabilitiesResponse TraceResponse(string status, int? exitCode = null)
    {
        var exitCodeField = exitCode.HasValue ? $",\"exitCode\":{exitCode.Value}" : string.Empty;
        var statusJson = $"{{\"schemaVersion\":2,\"status\":\"{status}\",\"targetVersion\":\"1.8.7\",\"installMethod\":\"tarball\",\"updatedAt\":\"2026-06-01T10:01:00Z\"{exitCodeField}}}";

        var eventsJsonl =
            "{\"t\":\"2026-06-01T10:00:00Z\",\"phase\":\"A\",\"kind\":\"start\",\"msg\":\"Selecting upgrade method\"}\n" +
            "{\"t\":\"2026-06-01T10:01:00Z\",\"phase\":\"B\",\"kind\":\"done\",\"msg\":\"finished\"}";

        return new CapabilitiesResponse
        {
            AgentVersion = "1.8.7",
            SupportedServices = new List<string> { "IScriptService/v1" },
            Metadata = new Dictionary<string, string>
            {
                ["upgradeStatus"] = statusJson,
                ["upgradeEvents"] = eventsJsonl,
                ["upgradeLog"] = "=== In scope ===\nRestarting service...\nUpgrade successful"
            }
        };
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
