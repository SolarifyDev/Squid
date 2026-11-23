using Autofac;
using Halibut;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using Squid.Core.Settings.Halibut;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// An agent downgraded from V2 to V1 (UpgradeMachineCommand supports downgrades), driven through the real
/// pipeline + real <c>HalibutMachineExecutionStrategy</c> + real selector / dispatchers / observer /
/// capabilities refresher + real Postgres (checkpoint and machine row). Only the agent transports are faked: a V2
/// service that answers every call with the plain transport exception a V1-only agent produces, a V1 service, and a
/// capabilities service. Pins that the deployment neither pauses forever nor re-runs the script, and that another
/// pod's capabilities snapshot is honoured before a V2 call is ever attempted.
/// </summary>
public class IntegrationScriptServiceV2Downgrade : DeploymentFixtureBase
{
    private static readonly string[] V1Only = { "IScriptService/v1" };
    private static readonly string[] BothVersions = { "IScriptService/v1", ScriptServiceCapabilities.V2 };

    [Fact]
    public async Task Downgrade_ResumeOfAV2Ticket_ThatIsStillLive_ReattachesOverV1_AndSucceeds_WithoutRerunning()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents();
        var cache = StaleCacheAdvertisingV2(seeded);

        await RunPipelineAsync(seeded, agents, cache, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await AssertStateAsync(seeded.TaskId, TaskState.Paused).ConfigureAwait(false);
        var ticket = agents.V2.StartedTickets.Single();

        agents.V2.Downgrade();
        agents.Capabilities.Services = V1Only;
        agents.V1.Know(ticket);
        agents.V1.Finish();
        await RunPipelineAsync(seeded, agents, cache, TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
        agents.V2.StartedTickets.Count.ShouldBe(1, "the script ran once, over V2, before the downgrade");
        agents.V1.StartedTickets.ShouldBeEmpty("an unreleased ticket visible to V1 is observed, never started again");
        agents.V1.ProbedTickets.ShouldContain(ticket);
        (await InFlightJsonAsync(seeded).ConfigureAwait(false)).ShouldBeNull("the pointer is released with the finished deployment");
        (await PersistedCapabilitiesAsync(seeded).ConfigureAwait(false)).SupportsScriptServiceV2.ShouldBeFalse("the answer the agent gave is written to the shared store for the other pods");
        await AssertBreakerClosedAsync(seeded).ConfigureAwait(false);
    }

    [Fact]
    public async Task Downgrade_ResumeOfAV2Ticket_V1DoesNotKnow_FailsTheAction_NeverRerunsIt_AndDoesNotPauseAgain()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents();
        var cache = StaleCacheAdvertisingV2(seeded);

        await RunPipelineAsync(seeded, agents, cache, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await AssertStateAsync(seeded.TaskId, TaskState.Paused).ConfigureAwait(false);

        agents.V2.Downgrade();
        agents.Capabilities.Services = V1Only;
        await Should.ThrowAsync<Exception>(() => RunPipelineAsync(seeded, agents, cache, TimeSpan.FromSeconds(60))).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Failed).ConfigureAwait(false);
        agents.V2.StartedTickets.Count.ShouldBe(1);
        agents.V1.StartedTickets.ShouldBeEmpty("a V2 ticket V1 cannot find may have run: it fails the action instead of running again");
        await AssertBreakerClosedAsync(seeded).ConfigureAwait(false);
    }

    [Fact]
    public async Task Downgrade_FreshDispatch_WithAStaleCache_FallsBackToV1_AndSucceeds()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents();
        agents.V2.Downgrade();
        agents.Capabilities.Services = V1Only;
        agents.V1.Finish();

        await RunPipelineAsync(seeded, agents, StaleCacheAdvertisingV2(seeded), TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
        agents.V1.StartedTickets.Count.ShouldBe(1);
        agents.V2.StartedTickets.ShouldBeEmpty();
        await AssertBreakerClosedAsync(seeded).ConfigureAwait(false);
    }

    [Fact]
    public async Task AnotherPodsV1OnlySnapshot_IsAdoptedBeforeDispatch_SoNoV2CallIsEverMade()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents();
        agents.V1.Finish();
        var cache = StaleCacheAdvertisingV2(seeded);

        // "Pod A" saw the agent after its downgrade and wrote the shared snapshot; this pod's cache is older.
        await Run<IMachineCapabilitiesRecorder>(r => r.RecordAsync(seeded.DispatchSlot.MachineId, new CapabilitiesResponse { AgentVersion = "1.0.0", SupportedServices = V1Only.ToList(), Metadata = new Dictionary<string, string> { ["os"] = "Linux" } }, default)).ConfigureAwait(false);

        await RunPipelineAsync(seeded, agents, cache, TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
        agents.V2OpenedClients.ShouldBe(0, "the shared snapshot is adopted before the version is chosen; V2 is never tried");
        agents.V1.StartedTickets.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnUpgradeInvalidationOnAnotherPod_EvictsThisPodsV2Entry_SoItDispatchesOverV1()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents();
        agents.V1.Finish();
        var machineId = seeded.DispatchSlot.MachineId;

        await Run<IMachineCapabilitiesRecorder>(r => r.RecordAsync(machineId, new CapabilitiesResponse { SupportedServices = BothVersions.ToList(), Metadata = new Dictionary<string, string> { ["os"] = "Linux" } }, default)).ConfigureAwait(false);
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        cache.Store(machineId, new Dictionary<string, string> { ["os"] = "Linux" }, "2.0.0", BothVersions, DateTimeOffset.UtcNow.AddMinutes(-1));

        await Run<IMachineRuntimeCapabilitiesPersistence>(p => p.InvalidateAsync(machineId, default)).ConfigureAwait(false);
        await RunPipelineAsync(seeded, agents, cache, TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
        agents.V2OpenedClients.ShouldBe(0);
        agents.V1.StartedTickets.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DependencyInjection_RegistersTheCapabilitiesRefresher_AndRecorder()
    {
        // The strategy's refresher is an optional ctor param (= null): a registration that stopped resolving would
        // silently turn every downgrade back into a permanent pause with every unit test still green.
        await Run<IMachineCapabilitiesRefresher, IMachineCapabilitiesRecorder, IMachineCapabilitiesStoreAccess>((refresher, recorder, store) =>
        {
            refresher.ShouldBeOfType<MachineCapabilitiesRefresher>();
            recorder.ShouldBeOfType<MachineCapabilitiesRecorder>();
            store.ShouldBeOfType<MachineCapabilitiesStoreAccess>();

            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    private static InMemoryMachineRuntimeCapabilitiesCache StaleCacheAdvertisingV2(SeededDeployment seeded)
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        cache.Store(seeded.DispatchSlot.MachineId, new Dictionary<string, string> { ["os"] = "Linux" }, "2.0.0", BothVersions, DateTimeOffset.UtcNow);

        return cache;
    }

    private async Task RunPipelineAsync(SeededDeployment seeded, Agents agents, InMemoryMachineRuntimeCapabilitiesCache cache, TimeSpan timeout)
    {
        // Short retry budgets: a V2 call to a V1-only agent is retried inside them before the strategy asks the agent what it speaks.
        var halibut = new HalibutSetting { RpcRetry = new RpcRetrySettings { BudgetSeconds = 1, CancelBudgetSeconds = 1, CompleteBudgetSeconds = 1 } };

        await Run<ILifetimeScope>(async scope =>
        {
            var runner = new DeploymentPipelineRunner(
                scope.Resolve<IEnumerable<IDeploymentPipelinePhase>>(),
                scope.Resolve<IDeploymentLifecycle>(),
                scope.Resolve<IDeploymentCompletionHandler>(),
                scope.Resolve<ITaskCancellationRegistry>(),
                scope.Resolve<IServerTaskDataProvider>(),
                scope.Resolve<ISquidBackgroundJobClient>())
            {
                DeploymentTimeout = timeout,
                TimeoutResumable = true
            };

            await runner.ProcessAsync(seeded.TaskId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        }, extraRegistration: b =>
        {
            b.RegisterInstance(cache).As<IMachineRuntimeCapabilitiesCache>().SingleInstance();
            b.RegisterInstance(halibut).AsSelf().SingleInstance();
            b.RegisterInstance(new Factories(agents)).As<IHalibutClientFactory>().As<IScriptServiceV2ClientFactory>().SingleInstance();
        }).ConfigureAwait(false);
    }

    private Task AssertStateAsync(int taskId, string expected)
        => Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(taskId, CancellationToken.None).ConfigureAwait(false);

            task.State.ShouldBe(expected);
        });

    private Task<string> InFlightJsonAsync(SeededDeployment seeded)
        => Run<IDeploymentCheckpointService, string>(async checkpoints => (await checkpoints.LoadAsync(seeded.TaskId).ConfigureAwait(false))?.InFlightScriptsJson);

    private Task<MachineRuntimeCapabilities> PersistedCapabilitiesAsync(SeededDeployment seeded)
        => Run<IMachineRuntimeCapabilitiesPersistence, MachineRuntimeCapabilities>(async persistence => (await persistence.LoadIfNewerAsync(seeded.DispatchSlot.MachineId, null, default).ConfigureAwait(false)).Capabilities);

    private Task AssertBreakerClosedAsync(SeededDeployment seeded)
        => Run<IMachineCircuitBreakerRegistry>(registry =>
        {
            registry.GetOrCreate(seeded.DispatchSlot.MachineId).ConsecutiveFailures.ShouldBe(0, "a protocol mismatch is not an agent outage and must not charge the machine's breaker");

            return Task.CompletedTask;
        });

    private sealed class Agents
    {
        public DowngradableV2Agent V2 { get; } = new();
        public LiveTicketV1Agent V1 { get; } = new();
        public ScriptedCapabilities Capabilities { get; } = new();
        public int V2OpenedClients => V2.OpenedClients;
    }

    private sealed class Factories : IHalibutClientFactory, IScriptServiceV2ClientFactory
    {
        private readonly Agents _agents;

        public Factories(Agents agents) => _agents = agents;

        public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => _agents.V1;

        public IAsyncScriptServiceV2 CreateScriptServiceV2Client(ServiceEndPoint endpoint)
        {
            _agents.V2.OpenedClients++;
            return _agents.V2;
        }

        public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => _agents.Capabilities;

        public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint) => throw new NotSupportedException();
    }

    private sealed class ScriptedCapabilities : IAsyncCapabilitiesService
    {
        public string[] Services { get; set; } = BothVersions;

        public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request)
            => Task.FromResult(new CapabilitiesResponse { AgentVersion = "1.0.0", SupportedServices = Services.ToList(), Metadata = new Dictionary<string, string> { ["os"] = "Linux" } });
    }

    // Runs the script until the test ends; after Downgrade() every call fails like a V1-only agent answering a V2 call.
    private sealed class DowngradableV2Agent : IAsyncScriptServiceV2
    {
        private volatile bool _downgraded;

        public int OpenedClients { get; set; }
        public List<string> StartedTickets { get; } = new();

        public void Downgrade() => _downgraded = true;

        public Task<ScriptStatusResponseV2> StartScriptAsync(StartScriptCommandV2 command)
        {
            ThrowIfDowngraded();
            lock (StartedTickets) StartedTickets.Add(command.ScriptTicket.TaskId);

            return Task.FromResult(Resp(command.ScriptTicket, ScriptStateV2.Running));
        }

        public Task<ScriptStatusResponseV2> GetStatusAsync(ScriptStatusRequestV2 request)
        {
            ThrowIfDowngraded();

            return Task.FromResult(Resp(request.Ticket, ScriptStateV2.Running));
        }

        public Task<ScriptStatusResponseV2> CancelScriptAsync(CancelScriptCommandV2 command)
        {
            ThrowIfDowngraded();

            return Task.FromResult(Resp(command.Ticket, ScriptStateV2.Completed));
        }

        public Task<CompleteScriptResponseV2> CompleteScriptAsync(CompleteScriptCommandV2 command)
        {
            ThrowIfDowngraded();

            return Task.FromResult(new CompleteScriptResponseV2(command.Ticket, CompleteScriptOutcomeV2.Completed, Resp(command.Ticket, ScriptStateV2.Completed)));
        }

        private void ThrowIfDowngraded()
        {
            if (_downgraded) throw new HalibutClientException("Unable to read data from the transport connection: read past the end of the stream.");
        }

        private static ScriptStatusResponseV2 Resp(ScriptTicket ticket, ScriptStateV2 state) => new(ticket, state, 0, new List<ProcessOutput>(), 0);
    }

    // Like the real Tentacle's V1 service over the shared core: a ticket it holds is Running until Finish(); any other ticket is Complete + UnknownResult (-1).
    private sealed class LiveTicketV1Agent : IAsyncScriptService
    {
        private readonly HashSet<string> _known = new();
        private volatile bool _finished;

        public List<string> StartedTickets { get; } = new();
        public List<string> ProbedTickets { get; } = new();

        public void Know(string ticket)
        {
            lock (_known) _known.Add(ticket);
        }

        public void Finish() => _finished = true;

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command)
        {
            lock (StartedTickets) StartedTickets.Add(command.ScriptTicket.TaskId);
            Know(command.ScriptTicket.TaskId);

            return Task.FromResult(Resp(command.ScriptTicket, ProcessState.Running, 0));
        }

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
        {
            lock (ProbedTickets) ProbedTickets.Add(request.Ticket.TaskId);

            bool known;
            lock (_known) known = _known.Contains(request.Ticket.TaskId);

            if (!known) return Task.FromResult(Resp(request.Ticket, ProcessState.Complete, ScriptExitCodes.UnknownResult));

            return Task.FromResult(Resp(request.Ticket, _finished ? ProcessState.Complete : ProcessState.Running, 0));
        }

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command) => Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 0));

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) => Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 0));

        private static ScriptStatusResponse Resp(ScriptTicket ticket, ProcessState state, int exitCode) => new(ticket, state, exitCode, new List<ProcessOutput>(), 0);
    }
}
