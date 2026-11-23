using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// The dispatch selector chooses ScriptService V2 only for an agent whose CACHED capabilities advertise it, and only a health check
/// fills the cache. Only the Tentacle health check used to; a Kubernetes agent (which serves V2 through its script-pod service) was
/// never recorded and so was dispatched over V1 forever. These pin that its health check records what the agent said — and that
/// the selector then picks V2 from it.
/// </summary>
public class KubernetesAgentCapabilitiesRecordingTests
{
    private const int MachineId = 31;

    private readonly Mock<IHalibutClientFactory> _factory = new();
    private readonly Mock<IAsyncCapabilitiesService> _caps = new();
    private readonly Mock<IMachineRuntimeCapabilitiesPersistence> _persistence = new();
    private readonly InMemoryMachineRuntimeCapabilitiesCache _cache = new();
    private readonly Machine _machine = new() { Id = MachineId, Name = "k8s-agent", Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-31","Thumbprint":"AABB"}""" };

    public KubernetesAgentCapabilitiesRecordingTests()
    {
        _factory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>())).Returns(_caps.Object);
    }

    private KubernetesAgentHealthCheckStrategy Strategy()
        => new(_factory.Object, new MachineCapabilitiesRecorder(_cache, new DirectStoreAccess(_persistence.Object)));

    private static CapabilitiesResponse Agent(Dictionary<string, string> metadata = null, params string[] services)
        => new() { AgentVersion = "2.0.0", SupportedServices = new List<string>(services), Metadata = metadata ?? new Dictionary<string, string> { ["os"] = "Linux" } };

    [Fact]
    public async Task HealthyAgentAdvertisingV2_IsRecorded_AndTheSelectorThenDispatchesOverV2()
    {
        _caps.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>())).ReturnsAsync(Agent(null, "IScriptService/v1", ScriptServiceCapabilities.V2));

        var result = await Strategy().CheckHealthAsync(_machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        _cache.TryGet(MachineId).SupportsScriptServiceV2.ShouldBeTrue();
        _cache.TryGet(MachineId).AgentVersion.ShouldBe("2.0.0");
        new ScriptDispatcherSelector(new IScriptDispatcher[] { new V1ScriptDispatcher(_factory.Object), new V2ScriptDispatcher(new Mock<IScriptServiceV2ClientFactory>().Object) }, _cache)
            .ForMachine(MachineId).Version.ShouldBe(ScriptProtocolVersion.V2);
        _persistence.Verify(p => p.SaveAsync(MachineId, It.Is<MachineRuntimeCapabilities>(c => c.SupportsScriptServiceV2), It.IsAny<CancellationToken>()), Times.Once, "the other pods read it from the shared store");
    }

    [Fact]
    public async Task AgentAdvertisingOnlyV1_IsRecordedAsV1()
    {
        _caps.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>())).ReturnsAsync(Agent(null, "IScriptService/v1"));

        await Strategy().CheckHealthAsync(_machine, null, CancellationToken.None);

        _cache.TryGet(MachineId).SupportsScriptServiceV2.ShouldBeFalse();
        _cache.TryGet(MachineId).SupportedServices.ShouldBe(new[] { "IScriptService/v1" });
    }

    [Fact]
    public async Task AgentWhoseRbacIsBroken_IsStillRecorded_TheProtocolHoldsRegardlessOfPermissions()
    {
        var metadata = new Dictionary<string, string> { ["os"] = "Linux", [KubernetesAgentHealthCheckStrategy.RbacCanCreatePodsKey] = "no" };
        _caps.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>())).ReturnsAsync(Agent(metadata, ScriptServiceCapabilities.V2));

        var result = await Strategy().CheckHealthAsync(_machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        _cache.TryGet(MachineId).SupportsScriptServiceV2.ShouldBeTrue();
    }

    [Fact]
    public async Task UnreachableAgent_RecordsNothing_SoACachedAdvertisementIsNotErased()
    {
        _cache.Store(MachineId, new Dictionary<string, string>(), "1.0.0", new[] { ScriptServiceCapabilities.V2 });
        _caps.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>())).ThrowsAsync(new HalibutClientException("down"));

        var result = await Strategy().CheckHealthAsync(_machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        _cache.TryGet(MachineId).SupportsScriptServiceV2.ShouldBeTrue();
        _persistence.Verify(p => p.SaveAsync(It.IsAny<int>(), It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PersistenceDown_DoesNotFailTheHealthCheck_AndTheCacheStillHasIt()
    {
        _persistence.Setup(p => p.SaveAsync(It.IsAny<int>(), It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        _caps.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>())).ReturnsAsync(Agent(null, ScriptServiceCapabilities.V2));

        var result = await Strategy().CheckHealthAsync(_machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        _cache.TryGet(MachineId).SupportsScriptServiceV2.ShouldBeTrue();
    }

    [Fact]
    public async Task ComposedWithoutARecorder_StillChecksHealth_AsBefore()
    {
        _caps.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>())).ReturnsAsync(Agent(null, ScriptServiceCapabilities.V2));

        var result = await new KubernetesAgentHealthCheckStrategy(_factory.Object).CheckHealthAsync(_machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
    }
}
