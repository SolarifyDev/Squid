using System.Linq;
using System.Text.Json;
using Squid.Core.Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Machines.Exceptions;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Requests.Machines;

namespace Squid.UnitTests.Services.Machines;

public class MachineServiceTests
{
    private readonly Mock<IMapper> _mapper = new();
    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
    private readonly Mock<IPollingTrustDistributor> _trustDistributor = new();
    private readonly InMemoryMachineRuntimeCapabilitiesCache _runtimeCache = new();
    private readonly MachineService _service;

    public MachineServiceTests()
    {
        _mapper.Setup(m => m.Map<MachineDto>(It.IsAny<Machine>()))
            .Returns<Machine>(m => new MachineDto { Id = m.Id, Name = m.Name, MachinePolicyId = m.MachinePolicyId });

        _mapper.Setup(m => m.Map<List<MachineDto>>(It.IsAny<List<Machine>>()))
            .Returns<List<Machine>>(list => list.Select(m => new MachineDto { Id = m.Id, Name = m.Name }).ToList());

        _service = new MachineService(_mapper.Object, _machineDataProvider.Object, _trustDistributor.Object, _runtimeCache);
    }

    // ========================================================================
    // GetMachinesAsync — AgentVersion enrichment from runtime capabilities cache
    // (FE Phase-2 §9.1: let the UI render "upgrade available" badges without
    // a follow-up round-trip per row).
    // ========================================================================

    [Fact]
    public async Task GetMachines_PopulatesAgentVersionFromCache()
    {
        var machines = new List<Machine>
        {
            new() { Id = 1, Name = "cached-1" },
            new() { Id = 2, Name = "cached-2" }
        };
        _machineDataProvider
            .Setup(p => p.GetMachinePagingAsync(It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((2, machines));

        _runtimeCache.Store(1, new Dictionary<string, string>(), agentVersion: "1.4.0");
        _runtimeCache.Store(2, new Dictionary<string, string>(), agentVersion: "1.3.9");

        var resp = await _service.GetMachinesAsync(new GetMachinesRequest(), CancellationToken.None);

        resp.Data.Machines.Single(m => m.Id == 1).AgentVersion.ShouldBe("1.4.0");
        resp.Data.Machines.Single(m => m.Id == 2).AgentVersion.ShouldBe("1.3.9");
    }

    [Fact]
    public async Task GetMachines_CacheMiss_AgentVersionIsEmptyString()
    {
        // Machine has never been health-checked → cache miss → empty string.
        // The FE treats empty as "version unknown" and hides the upgrade
        // badge rather than guessing.
        var machines = new List<Machine> { new() { Id = 1, Name = "never-checked" } };
        _machineDataProvider
            .Setup(p => p.GetMachinePagingAsync(It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((1, machines));
        // Note: no runtimeCache.Store → cache miss

        var resp = await _service.GetMachinesAsync(new GetMachinesRequest(), CancellationToken.None);

        resp.Data.Machines.Single().AgentVersion.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task GetMachines_MixedCacheState_EachRowIndependentlyEnriched()
    {
        // Realistic prod scenario: some machines cached (recently health-
        // checked), others cold. Must enrich per-row, not all-or-nothing.
        var machines = new List<Machine>
        {
            new() { Id = 10, Name = "warm" },
            new() { Id = 11, Name = "cold" },
            new() { Id = 12, Name = "also-warm" }
        };
        _machineDataProvider
            .Setup(p => p.GetMachinePagingAsync(It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((3, machines));

        _runtimeCache.Store(10, new Dictionary<string, string>(), agentVersion: "1.4.0");
        _runtimeCache.Store(12, new Dictionary<string, string>(), agentVersion: "1.4.0-rc.2");
        // id=11 intentionally not stored

        var resp = await _service.GetMachinesAsync(new GetMachinesRequest(), CancellationToken.None);

        resp.Data.Machines.Single(m => m.Id == 10).AgentVersion.ShouldBe("1.4.0");
        resp.Data.Machines.Single(m => m.Id == 11).AgentVersion.ShouldBe(string.Empty);
        resp.Data.Machines.Single(m => m.Id == 12).AgentVersion.ShouldBe("1.4.0-rc.2");
    }

    [Fact]
    public async Task UpdateMachine_ResponseDto_AlsoEnrichedWithAgentVersion()
    {
        // UpdateMachine response feeds the FE the updated row directly — must
        // be consistent with GetMachines enrichment so the UI doesn't lose
        // the version indicator post-update.
        var machine = new Machine { Id = 5, Name = "agent-5" };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(machine);
        _runtimeCache.Store(5, new Dictionary<string, string>(), agentVersion: "1.4.0");

        var response = await _service.UpdateMachineAsync(new UpdateMachineCommand { MachineId = 5, Name = "agent-5" }, CancellationToken.None);

        response.Data.AgentVersion.ShouldBe("1.4.0",
            "UpdateMachine response must carry the same AgentVersion the list would, so FE state stays coherent");
    }

    // ========================================================================
    // UpdateMachineAsync — ApplyUpdate
    // ========================================================================

    [Fact]
    public async Task UpdateMachine_ChangesMachinePolicyId()
    {
        var machine = new Machine { Id = 1, Name = "test", MachinePolicyId = 1 };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1, MachinePolicyId = 42 };

        var response = await _service.UpdateMachineAsync(command, CancellationToken.None);

        response.Data.MachinePolicyId.ShouldBe(42);
        _machineDataProvider.Verify(p => p.UpdateMachineAsync(It.Is<Machine>(m => m.MachinePolicyId == 42), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateMachine_ChangesName()
    {
        var machine = new Machine { Id = 1, Name = "old-name" };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1, Name = "new-name" };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        machine.Name.ShouldBe("new-name");
    }

    [Fact]
    public async Task UpdateMachine_ChangesIsDisabled()
    {
        var machine = new Machine { Id = 1, Name = "test", IsDisabled = false };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1, IsDisabled = true };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        machine.IsDisabled.ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateMachine_ChangesRolesAndEnvironments()
    {
        var machine = new Machine { Id = 1, Name = "test", Roles = "[]", EnvironmentIds = "[]" };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand
        {
            MachineId = 1,
            Roles = new List<string> { "k8s", "production" },
            EnvironmentIds = new List<int> { 1, 2 }
        };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        machine.Roles.ShouldBe(JsonSerializer.Serialize(new[] { "k8s", "production" }));
        machine.EnvironmentIds.ShouldBe(JsonSerializer.Serialize(new[] { 1, 2 }));
    }

    [Fact]
    public async Task UpdateMachine_NullFieldsNotOverwritten()
    {
        var machine = new Machine { Id = 1, Name = "keep-this", IsDisabled = false, MachinePolicyId = 5 };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1 };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        machine.Name.ShouldBe("keep-this");
        machine.IsDisabled.ShouldBeFalse();
        machine.MachinePolicyId.ShouldBe(5);
    }

    [Fact]
    public async Task UpdateMachine_MachineNotFound_Throws()
    {
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((Machine)null);

        var command = new UpdateMachineCommand { MachineId = 999 };

        await Should.ThrowAsync<InvalidOperationException>(() => _service.UpdateMachineAsync(command, CancellationToken.None));
    }

    // ========================================================================
    // UpdateMachineAsync — Trust Reconfiguration
    // ========================================================================

    [Fact]
    public async Task UpdateMachine_PersistsThenReconfiguresTrust()
    {
        var machine = new Machine { Id = 1, Name = "agent-1" };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var callOrder = new List<string>();
        _machineDataProvider
            .Setup(p => p.UpdateMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("persist"))
            .Returns(Task.CompletedTask);
        _trustDistributor
            .Setup(t => t.Reconfigure())
            .Callback(() => callOrder.Add("reconfigure"));

        var command = new UpdateMachineCommand { MachineId = 1, Name = "renamed" };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        callOrder.ShouldBe(new[] { "persist", "reconfigure" });
    }

    [Fact]
    public async Task UpdateMachine_NonPollingMachine_StillReconfigures()
    {
        var machine = new Machine { Id = 1, Name = "api-target" };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1, Name = "renamed" };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        _trustDistributor.Verify(t => t.Reconfigure(), Times.Once);
    }

    [Fact]
    public async Task UpdateMachine_DisablingMachine_ReconfiguresTrust()
    {
        var machine = new Machine { Id = 1, Name = "agent-1", IsDisabled = false };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1, IsDisabled = true };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        _trustDistributor.Verify(t => t.Reconfigure(), Times.Once);
    }

    // ========================================================================
    // UpdateMachineAsync — Endpoint Fields
    // ========================================================================

    [Fact]
    public async Task UpdateMachine_WithProviderType_EndpointJsonUpdated()
    {
        var existingEndpoint = JsonSerializer.Serialize(new KubernetesApiEndpointDto
        {
            ClusterUrl = "https://old.cluster.com",
            Namespace = "default",
            SkipTlsVerification = "False",
            CommunicationStyle = "KubernetesApi"
        });

        var machine = new Machine { Id = 1, Name = "test", Endpoint = existingEndpoint };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var providerConfig = JsonSerializer.Serialize(new KubernetesApiAwsEksConfig { ClusterName = "my-eks", Region = "us-west-2" });
        var command = new UpdateMachineCommand
        {
            MachineId = 1,
            ProviderType = Squid.Message.Enums.KubernetesApiEndpointProviderType.AwsEks,
            ProviderConfig = providerConfig,
        };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        var endpoint = JsonSerializer.Deserialize<KubernetesApiEndpointDto>(machine.Endpoint);
        endpoint.ProviderType.ShouldBe(Squid.Message.Enums.KubernetesApiEndpointProviderType.AwsEks);
        endpoint.ProviderConfig.ShouldBe(providerConfig);
        endpoint.ClusterUrl.ShouldBe("https://old.cluster.com");
    }

    [Fact]
    public async Task UpdateMachine_WithoutEndpointFields_EndpointUnchanged()
    {
        var existingEndpoint = "{\"ClusterUrl\":\"https://keep.me\"}";
        var machine = new Machine { Id = 1, Name = "test", Endpoint = existingEndpoint };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1, Name = "renamed" };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        machine.Endpoint.ShouldBe(existingEndpoint);
    }

    [Fact]
    public async Task UpdateMachine_WithClusterUrl_OnlyClusterUrlChanges()
    {
        var existingEndpoint = JsonSerializer.Serialize(new KubernetesApiEndpointDto
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = "https://old.cluster.com",
            Namespace = "production",
            SkipTlsVerification = "True",
        });

        var machine = new Machine { Id = 1, Name = "test", Endpoint = existingEndpoint };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1, ClusterUrl = "https://new.cluster.com" };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        var endpoint = JsonSerializer.Deserialize<KubernetesApiEndpointDto>(machine.Endpoint);
        endpoint.ClusterUrl.ShouldBe("https://new.cluster.com");
        endpoint.Namespace.ShouldBe("production");
        endpoint.SkipTlsVerification.ShouldBe("True");
    }

    [Theory]
    [InlineData("""{"CommunicationStyle":"KubernetesApi","ClusterUrl":"https://old.k8s","Namespace":"prod","SkipTlsVerification":"True"}""")]
    [InlineData("""{"communicationStyle":"KubernetesApi","clusterUrl":"https://old.k8s","namespace":"prod","skipTlsVerification":"True"}""")]
    public async Task UpdateMachine_ExistingEndpointBothCasings_MergesCorrectly(string existingEndpoint)
    {
        var machine = new Machine { Id = 1, Name = "test", Endpoint = existingEndpoint };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1, ClusterUrl = "https://new.k8s" };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        var endpoint = JsonSerializer.Deserialize<KubernetesApiEndpointDto>(machine.Endpoint);
        endpoint.ClusterUrl.ShouldBe("https://new.k8s");
        endpoint.Namespace.ShouldBe("prod");
        endpoint.SkipTlsVerification.ShouldBe("True");
    }

    // ========================================================================
    // Round-6 R6-A + R6-F — cross-style field contamination.
    //
    // The PRIMARY bug we're closing: previously `ApplyEndpointUpdate` had an
    // `else → K8s` fallthrough. A client updating a TentaclePolling machine
    // with a K8s field like `ResourceReferences` would cause the server to
    // deserialise the Tentacle endpoint JSON as `KubernetesApiEndpointDto`
    // and re-serialise — silently destroying SubscriptionId / Thumbprint /
    // Uri. Agent loses trust, operator must re-register.
    //
    // Fix: per-style `IMachineUpdateStrategy` with fail-fast contamination
    // detection BEFORE any mutation. These tests lock the contract in.
    // ========================================================================

    private static Machine MakeMachine(int id, string endpointJson) => new()
    {
        Id = id,
        Name = $"m-{id}",
        Endpoint = endpointJson,
        SpaceId = 1
    };

    private const string TentaclePollingEndpoint = """
        {"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-abc","Thumbprint":"DEAD123","AgentVersion":"1.4.0"}
        """;
    private const string TentacleListeningEndpoint = """
        {"CommunicationStyle":"TentacleListening","Uri":"https://10.0.0.5:10933","Thumbprint":"BEEF456","AgentVersion":"1.4.0"}
        """;
    private const string SshEndpoint = """
        {"CommunicationStyle":"Ssh","Host":"10.0.0.7","Port":22,"Fingerprint":"SHA256:xyz"}
        """;
    private const string KubernetesAgentEndpoint = """
        {"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-k8s","Thumbprint":"CAFE789","Namespace":"prod","ReleaseName":"squid","ChartRef":"oci://foo"}
        """;

    [Theory]
    // Tentacle machine + K8sApi-only field → must throw (the ORIGINAL bug)
    [InlineData(TentaclePollingEndpoint, nameof(UpdateMachineCommand.ClusterUrl), "https://k8s")]
    [InlineData(TentaclePollingEndpoint, nameof(UpdateMachineCommand.ProviderConfig), "{}")]
    // Tentacle Listening machine + K8s field
    [InlineData(TentacleListeningEndpoint, nameof(UpdateMachineCommand.ClusterUrl), "https://k8s")]
    // SSH machine + K8s field
    [InlineData(SshEndpoint, nameof(UpdateMachineCommand.ClusterUrl), "https://k8s")]
    // SSH machine + OpenClaw field
    [InlineData(SshEndpoint, nameof(UpdateMachineCommand.BaseUrl), "https://oc")]
    // K8sAgent machine + K8sApi-specific field
    [InlineData(KubernetesAgentEndpoint, nameof(UpdateMachineCommand.ClusterUrl), "https://k8s")]
    // K8sAgent + SSH field
    [InlineData(KubernetesAgentEndpoint, nameof(UpdateMachineCommand.Host), "10.0.0.1")]
    // Tentacle + OpenClaw field
    [InlineData(TentaclePollingEndpoint, nameof(UpdateMachineCommand.BaseUrl), "https://oc")]
    public async Task UpdateMachine_CrossStyleFieldSent_ThrowsBeforeMutation(string endpointJson, string fieldName, string fieldValue)
    {
        var machine = MakeMachine(1, endpointJson);
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = BuildCommandWithField(1, fieldName, fieldValue);
        var originalEndpoint = machine.Endpoint;
        var originalName = machine.Name;

        var ex = await Should.ThrowAsync<MachineEndpointUpdateNotApplicableException>(() =>
            _service.UpdateMachineAsync(command, CancellationToken.None));

        ex.MachineId.ShouldBe(1);
        ex.OffendingField.ShouldBe(fieldName);
        machine.Endpoint.ShouldBe(originalEndpoint,
            "endpoint JSON MUST NOT be mutated when validation fails (the original bug corrupted it)");
        machine.Name.ShouldBe(originalName,
            "common fields MUST NOT be mutated either — validation fires before ANY apply step");
        _machineDataProvider.Verify(
            p => p.UpdateMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no persistence should happen when validation fails — avoids any chance of corrupt state landing in the DB");
    }

    [Fact]
    public async Task UpdateMachine_TentaclePolling_OwnFields_ApplyCleanly()
    {
        var machine = MakeMachine(1, TentaclePollingEndpoint);
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand
        {
            MachineId = 1,
            Thumbprint = "NEW_CERT",
            SubscriptionId = "new-sub"
        };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        var updated = JsonSerializer.Deserialize<TentaclePollingEndpointDto>(machine.Endpoint);
        updated.Thumbprint.ShouldBe("NEW_CERT");
        updated.SubscriptionId.ShouldBe("new-sub");
        updated.AgentVersion.ShouldBe("1.4.0",
            "AgentVersion in the endpoint JSON must be preserved — we only mutate fields the operator passed");
    }

    [Fact]
    public async Task UpdateMachine_TentacleListening_UriAndProxyId_ApplyCleanly()
    {
        var machine = MakeMachine(2, TentacleListeningEndpoint);
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand
        {
            MachineId = 2,
            Uri = "https://10.0.0.99:10933",
            ProxyId = 7,
        };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        var updated = JsonSerializer.Deserialize<TentacleListeningEndpointDto>(machine.Endpoint);
        updated.Uri.ShouldBe("https://10.0.0.99:10933");
        updated.ProxyId.ShouldBe(7);
        updated.Thumbprint.ShouldBe("BEEF456", "unchanged Thumbprint must survive partial update");
    }

    [Fact]
    public async Task UpdateMachine_Ssh_HostAndPort_ApplyCleanly()
    {
        var machine = MakeMachine(3, SshEndpoint);
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand
        {
            MachineId = 3,
            Host = "10.0.0.99",
            Port = 2222,
            ProxyUsername = "bob",
        };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        var updated = JsonSerializer.Deserialize<SshEndpointDto>(machine.Endpoint);
        updated.Host.ShouldBe("10.0.0.99");
        updated.Port.ShouldBe(2222);
        updated.ProxyUsername.ShouldBe("bob");
        updated.Fingerprint.ShouldBe("SHA256:xyz", "existing Fingerprint must survive partial update");
    }

    [Fact]
    public async Task UpdateMachine_KubernetesAgent_ChartRefUpdate_ApplyCleanly()
    {
        var machine = MakeMachine(4, KubernetesAgentEndpoint);
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand
        {
            MachineId = 4,
            ChartRef = "oci://new-chart:1.5",
            HelmNamespace = "staging",
        };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        var updated = JsonSerializer.Deserialize<KubernetesAgentEndpointDto>(machine.Endpoint);
        updated.ChartRef.ShouldBe("oci://new-chart:1.5");
        updated.HelmNamespace.ShouldBe("staging");
        updated.SubscriptionId.ShouldBe("sub-k8s", "identity fields preserved through partial update");
        updated.Thumbprint.ShouldBe("CAFE789", "trust cert preserved through partial update");
    }

    [Fact]
    public async Task UpdateMachine_TentaclePolling_OnlyCommonFields_NoEndpointMutation()
    {
        // Renaming a Tentacle machine should work — it's a common field.
        // Nothing style-specific is sent, so endpoint JSON is untouched.
        var machine = MakeMachine(5, TentaclePollingEndpoint);
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 5, Name = "renamed-agent", IsDisabled = true };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        machine.Name.ShouldBe("renamed-agent");
        machine.IsDisabled.ShouldBeTrue();
        machine.Endpoint.ShouldBe(TentaclePollingEndpoint,
            "no style-specific fields in command → endpoint JSON is byte-for-byte identical; no deserialise/reserialise cycle");
    }

    [Fact]
    public async Task UpdateMachine_ShuffledEndpointJson_CommonFieldsOnly_NoSilentNormalisation()
    {
        // Round-7 regression guard: before the short-circuit, every update
        // ran deserialise → merge → re-serialise which silently rewrote the
        // JSON in DTO property order. Real DB data (hand-edited, migrated,
        // or produced by an older serialiser) can use different property
        // order. A rename-only update must NOT touch such JSON — operators
        // would see unexplained diffs in audit logs.
        const string shuffledJson = """
            {"AgentVersion":"1.4.0","Thumbprint":"DEAD123","CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-abc"}
            """;
        var machine = MakeMachine(50, shuffledJson);
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        await _service.UpdateMachineAsync(
            new UpdateMachineCommand { MachineId = 50, Name = "rename-only" },
            CancellationToken.None);

        machine.Endpoint.ShouldBe(shuffledJson,
            "shuffled-order endpoint JSON MUST survive a common-fields-only update byte-identically — no silent JSON normalisation");
    }

    [Fact]
    public async Task UpdateMachine_KubernetesApi_StyleFieldSet_EndpointGetsRoundTripped()
    {
        // Counterpart to the short-circuit test: when a style-specific
        // field IS set, the round-trip DOES happen (that's how the merge
        // works). This pins the invariant from the other direction — no
        // accidental over-eager short-circuiting that skips real updates.
        const string k8sJson = """
            {"CommunicationStyle":"KubernetesApi","ClusterUrl":"https://old","Namespace":"default"}
            """;
        var machine = MakeMachine(51, k8sJson);
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(51, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        await _service.UpdateMachineAsync(
            new UpdateMachineCommand { MachineId = 51, ClusterUrl = "https://new" },
            CancellationToken.None);

        var updated = JsonSerializer.Deserialize<KubernetesApiEndpointDto>(machine.Endpoint);
        updated.ClusterUrl.ShouldBe("https://new");
        updated.Namespace.ShouldBe("default", "unchanged Namespace must be preserved through the merge");
    }

    [Fact]
    public async Task UpdateMachine_UnknownStyle_AnyStyleField_FailsLoudly_NotCorrupts()
    {
        // Previously an unknown-style endpoint would still fall into the
        // K8s apply path via the `else`. Now: fail-loud with clear error.
        var machine = MakeMachine(6, """{"CommunicationStyle":"FancyNewStyle2026","foo":"bar"}""");
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(6, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 6, ClusterUrl = "https://evil" };
        var original = machine.Endpoint;

        await Should.ThrowAsync<MachineEndpointUpdateNotApplicableException>(() =>
            _service.UpdateMachineAsync(command, CancellationToken.None));

        machine.Endpoint.ShouldBe(original, "endpoint JSON must be byte-identical on rejection");
    }

    [Fact]
    public async Task UpdateMachine_ExceptionMessage_NamesFieldAndExpectedStyle_SoOperatorKnowsWhereItBelongs()
    {
        // Error ergonomics — message must tell the user "ClusterUrl belongs
        // to KubernetesApi machines, not TentaclePolling" so they fix their
        // request rather than guess.
        var machine = MakeMachine(7, TentaclePollingEndpoint);
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 7, ClusterUrl = "https://cluster" };

        var ex = await Should.ThrowAsync<MachineEndpointUpdateNotApplicableException>(() =>
            _service.UpdateMachineAsync(command, CancellationToken.None));

        ex.Message.ShouldContain("ClusterUrl");
        ex.Message.ShouldContain("TentaclePolling");
        ex.Message.ShouldContain("KubernetesApi",
            customMessage: "message must tell operator which style ClusterUrl belongs to");
    }

    [Fact]
    public async Task UpdateMachine_MachineNotFound_ThrowsTypedExceptionForHttp404()
    {
        // Round-6 — tightened from generic InvalidOperationException to the
        // typed MachineNotFoundException so GlobalExceptionFilter can map
        // it to HTTP 404 instead of 500.
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(9999, It.IsAny<CancellationToken>())).ReturnsAsync((Machine)null);

        var ex = await Should.ThrowAsync<MachineNotFoundException>(() =>
            _service.UpdateMachineAsync(new UpdateMachineCommand { MachineId = 9999 }, CancellationToken.None));

        ex.MachineId.ShouldBe(9999);
    }

    [Fact]
    public async Task UpdateMachine_NameConflict_ThrowsTypedExceptionForHttp409()
    {
        // Round-6 — same tightening for rename conflict.
        var machine = MakeMachine(10, TentaclePollingEndpoint);
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(machine);
        _machineDataProvider.Setup(p => p.ExistsByNameAsync("taken-name", 1, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var ex = await Should.ThrowAsync<MachineNameConflictException>(() =>
            _service.UpdateMachineAsync(new UpdateMachineCommand { MachineId = 10, Name = "taken-name" }, CancellationToken.None));

        ex.MachineName.ShouldBe("taken-name");
    }

    private static UpdateMachineCommand BuildCommandWithField(int machineId, string fieldName, string fieldValue)
    {
        var cmd = new UpdateMachineCommand { MachineId = machineId };

        // Minimal reflection — set one field by name so Theory cases are compact.
        var prop = typeof(UpdateMachineCommand).GetProperty(fieldName)
            ?? throw new InvalidOperationException($"UpdateMachineCommand has no property '{fieldName}' — test case typo?");

        if (prop.PropertyType == typeof(string)) prop.SetValue(cmd, fieldValue);
        else if (prop.PropertyType == typeof(int?)) prop.SetValue(cmd, int.Parse(fieldValue));
        else throw new InvalidOperationException($"test helper doesn't handle {prop.PropertyType} — extend as needed");

        return cmd;
    }

    // ========================================================================
    // DeleteMachinesAsync — Trust Reconfiguration
    // ========================================================================

    [Fact]
    public async Task DeleteMachines_PersistsThenReconfiguresTrust()
    {
        var machines = new List<Machine> { new() { Id = 1, Name = "agent-1" } };
        _machineDataProvider.Setup(p => p.GetMachinesByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(machines);

        var callOrder = new List<string>();
        _machineDataProvider
            .Setup(p => p.DeleteMachinesAsync(It.IsAny<List<Machine>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("persist"))
            .Returns(Task.CompletedTask);
        _trustDistributor
            .Setup(t => t.Reconfigure())
            .Callback(() => callOrder.Add("reconfigure"));

        var command = new DeleteMachinesCommand { Ids = new List<int> { 1 } };

        await _service.DeleteMachinesAsync(command, CancellationToken.None);

        callOrder.ShouldBe(new[] { "persist", "reconfigure" });
    }
}

internal static class TestCertHelper
{
    public static (byte[] pfxBytes, string password) GenerateSelfSignedPfx()
    {
        const string password = "test";
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=Test", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        return (cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, password), password);
    }

    public static (byte[] derBytes, string password) GenerateSelfSignedDer()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=Test", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        return (cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert), null);
    }
}
