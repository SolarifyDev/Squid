using System.Text.Json;
using Autofac;
using Halibut;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.IntegrationTests.Base;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.IntegrationTests.Services.DeploymentExecution;

/// <summary>
/// A Kubernetes agent's health check, resolved from the real container, records the capabilities it read (real Postgres + the real
/// process-wide cache), and another pod that never probed the agent then picks ScriptService V2 for it from the shared store. Before
/// the fix the Kubernetes health check stored nothing, so a Kubernetes agent was dispatched over V1 on every pod forever.
/// </summary>
public class KubernetesAgentCapabilitiesIntegrationTests : TestBase
{
    public KubernetesAgentCapabilitiesIntegrationTests() : base("KubernetesAgentCapabilities", "squid_it_k8s_agent_caps")
    {
    }

    [Fact]
    public async Task HealthCheck_RecordsTheAgentsCapabilities_InTheCacheAndTheSharedStore_AndAnotherPodThenSelectsV2()
    {
        var machine = await InsertMachineAsync().ConfigureAwait(false);
        var caps = new ScriptedCapabilities(new CapabilitiesResponse { AgentVersion = "2.1.0", SupportedServices = new List<string> { "IScriptService/v1", ScriptServiceCapabilities.V2 }, Metadata = new Dictionary<string, string> { ["os"] = "Linux" } });

        var result = await Run<KubernetesAgentHealthCheckStrategy, HealthCheckResult>(s => s.CheckHealthAsync(machine, null, CancellationToken.None), b => b.RegisterInstance(new Factory(caps)).As<IHalibutClientFactory>().SingleInstance()).ConfigureAwait(false);

        result.Healthy.ShouldBeTrue();

        var persisted = await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(machine.Id, null, default)).ConfigureAwait(false);
        persisted.ShouldNotBeNull("the health check must write the shared snapshot, not only this pod's cache");
        persisted.Capabilities.SupportsScriptServiceV2.ShouldBeTrue();

        var otherPod = new InMemoryMachineRuntimeCapabilitiesCache();
        await Run<IMachineCapabilitiesStoreAccess>(async store => await new MachineCapabilitiesRefresher(null, otherPod, store, new MachineCapabilitiesRecorder(otherPod, store), new Squid.Core.Settings.Halibut.HalibutSetting()).SyncFromStoreAsync(machine.Id, default).ConfigureAwait(false)).ConfigureAwait(false);

        new ScriptDispatcherSelector(new IScriptDispatcher[] { new Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers.V1ScriptDispatcher(null), new Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers.V2ScriptDispatcher(null) }, otherPod)
            .ForMachine(machine.Id).Version.ShouldBe(ScriptProtocolVersion.V2, "a pod that never probed the agent dispatches over V2 from the shared snapshot");
    }

    [Fact]
    public async Task HealthCheck_OfAnAgentThatCannotBeReached_WritesNothing()
    {
        var machine = await InsertMachineAsync().ConfigureAwait(false);

        var result = await Run<KubernetesAgentHealthCheckStrategy, HealthCheckResult>(s => s.CheckHealthAsync(machine, null, CancellationToken.None), b => b.RegisterInstance(new Factory(new ScriptedCapabilities(null))).As<IHalibutClientFactory>().SingleInstance()).ConfigureAwait(false);

        result.Healthy.ShouldBeFalse();
        (await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(machine.Id, null, default)).ConfigureAwait(false)).ShouldBeNull();
    }

    private async Task<Machine> InsertMachineAsync()
    {
        Machine machine = null;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            machine = new Machine
            {
                Name = $"k8s-caps-{Guid.NewGuid():N}",
                IsDisabled = false,
                Roles = "[]",
                EnvironmentIds = "[]",
                Endpoint = JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesAgent", SubscriptionId = $"sub-{Guid.NewGuid():N}", Thumbprint = "AA11BB22" }),
                SpaceId = 1,
                Slug = $"k8s-caps-{Guid.NewGuid():N}"[..20]
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        return machine;
    }

    private sealed class ScriptedCapabilities : IAsyncCapabilitiesService
    {
        private readonly CapabilitiesResponse _answer;

        public ScriptedCapabilities(CapabilitiesResponse answer) => _answer = answer;

        public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request)
            => _answer == null ? throw new HalibutClientException("agent unreachable") : Task.FromResult(_answer);
    }

    private sealed class Factory : IHalibutClientFactory
    {
        private readonly IAsyncCapabilitiesService _caps;

        public Factory(IAsyncCapabilitiesService caps) => _caps = caps;

        public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => throw new NotSupportedException();

        public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => _caps;

        public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint) => throw new NotSupportedException();
    }
}
