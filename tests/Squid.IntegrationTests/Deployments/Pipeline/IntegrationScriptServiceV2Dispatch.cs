using Autofac;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Full-pipeline coverage (real <c>DeploymentPipelineRunner</c> + real <c>ExecuteStepsPhase</c> + real
/// <c>HalibutMachineExecutionStrategy</c> + real selector / dispatchers / observer + real Postgres
/// checkpoint; only the agent transports are faked) of capability-negotiated ScriptService V2 dispatch:
/// a V2-advertising agent is dispatched over V2 and the version is persisted in the checkpoint; a pause
/// followed by a resume re-attaches over V2 with the SAME ticket; a lost result fails the action without a
/// second dispatch; and a record written by a V1 dispatch resumes over V1 even after the agent started
/// advertising V2.
/// </summary>
public class IntegrationScriptServiceV2Dispatch : DeploymentFixtureBase
{
    [Fact]
    public async Task AdvertisedV2_Deploys_OverV2Only_AndSucceeds()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents { V2 = new BlockingV2Agent(), V1 = new IntegrationPausePreservesRemoteScript.BlockingAgent() };
        agents.V2.Finish();

        await RunPipelineAsync(seeded, agents, advertiseV2: true, timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
        agents.V2.StartedTickets.Count.ShouldBe(1);
        agents.V1.StartedTickets.ShouldBeEmpty("an agent advertising V2 must be dispatched over V2 only");
        (await InFlightJsonAsync(seeded).ConfigureAwait(false)).ShouldBeNull("the checkpoint is gone after a successful deployment");
    }

    [Fact]
    public async Task ColdCache_Deploys_OverV1_AndNeverOpensAV2Client()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents { V2 = new BlockingV2Agent(), V1 = new IntegrationPausePreservesRemoteScript.BlockingAgent() };
        agents.V1.Finish();

        await RunPipelineAsync(seeded, agents, advertiseV2: false, timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
        agents.V1.StartedTickets.Count.ShouldBe(1);
        agents.V2OpenedClients.ShouldBe(0, "V1 is chosen from the advertised capabilities, never by trying V2 first");
    }

    [Fact]
    public async Task V2_PauseThenResume_ReattachesOverV2_WithTheSameTicket_AndRecordsTheVersion()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents { V2 = new BlockingV2Agent(), V1 = new IntegrationPausePreservesRemoteScript.BlockingAgent() };

        await RunPipelineAsync(seeded, agents, advertiseV2: true, timeout: TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Paused).ConfigureAwait(false);
        agents.V2.CancelCalls.ShouldBe(0, "a paused task must leave its script running");
        var json = await InFlightJsonAsync(seeded).ConfigureAwait(false);
        InFlightScriptMap.TryGet(json, seeded.DispatchSlot).ShouldBe(agents.V2.StartedTickets.Single());
        InFlightScriptMap.GetProtocol(json, seeded.DispatchSlot).ShouldBe(ScriptProtocolVersion.V2, "the version is persisted with the ticket, in the same write");

        agents.V2.Finish();
        await RunPipelineAsync(seeded, agents, advertiseV2: true, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
        agents.V2.StartedTickets.Count.ShouldBe(1, "resume must re-attach, not dispatch a duplicate");
        agents.V2.ProbedTickets.ShouldAllBe(t => t == agents.V2.StartedTickets[0]);
        agents.V1.StartedTickets.ShouldBeEmpty();
    }

    [Fact]
    public async Task V2_ResumeAfterTheAgentStoppedAdvertisingV2_StillReattachesOverV2_NeverV1()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents { V2 = new BlockingV2Agent(), V1 = new IntegrationPausePreservesRemoteScript.BlockingAgent() };

        await RunPipelineAsync(seeded, agents, advertiseV2: true, timeout: TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await AssertStateAsync(seeded.TaskId, TaskState.Paused).ConfigureAwait(false);

        agents.V2.Finish();
        await RunPipelineAsync(seeded, agents, advertiseV2: false, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
        agents.V2.StartedTickets.Count.ShouldBe(1);
        agents.V1.ProbedTickets.ShouldBeEmpty("the script lives in the agent's V2 state; probing it over V1 would read 'unknown' and re-run it");
        agents.V1.StartedTickets.ShouldBeEmpty();
    }

    [Fact]
    public async Task V2_ResultLostOnResume_FailsTheAction_WithoutRedispatching()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents { V2 = new BlockingV2Agent(), V1 = new IntegrationPausePreservesRemoteScript.BlockingAgent() };

        await RunPipelineAsync(seeded, agents, advertiseV2: true, timeout: TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await AssertStateAsync(seeded.TaskId, TaskState.Paused).ConfigureAwait(false);

        agents.V2.LoseResult();
        await Should.ThrowAsync<Exception>(() => RunPipelineAsync(seeded, agents, advertiseV2: true, timeout: TimeSpan.FromSeconds(30))).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Failed).ConfigureAwait(false);
        agents.V2.StartedTickets.Count.ShouldBe(1, "a script whose result was lost may have run: it must never be dispatched again");
        agents.V1.StartedTickets.ShouldBeEmpty();
    }

    [Fact]
    public async Task V1Record_ResumesOverV1_EvenAfterTheAgentStartedAdvertisingV2()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents { V2 = new BlockingV2Agent(), V1 = new IntegrationPausePreservesRemoteScript.BlockingAgent() };

        await RunPipelineAsync(seeded, agents, advertiseV2: false, timeout: TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Paused).ConfigureAwait(false);
        var json = await InFlightJsonAsync(seeded).ConfigureAwait(false);
        json.ShouldNotContain("\"v\"", customMessage: "a V1 record is byte-identical to the pre-negotiation shape (no version field)");
        InFlightScriptMap.GetProtocol(json, seeded.DispatchSlot).ShouldBe(ScriptProtocolVersion.V1);

        agents.V1.Finish();
        await RunPipelineAsync(seeded, agents, advertiseV2: true, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
        agents.V1.StartedTickets.Count.ShouldBe(1, "the record has no version, so it is a V1 script: re-attach over V1, never a second dispatch");
        agents.V1.ProbedTickets.ShouldAllBe(t => t == agents.V1.StartedTickets[0]);
        agents.V2.StartedTickets.ShouldBeEmpty();
        agents.V2.ProbedTickets.ShouldBeEmpty("the legacy record must not be probed over V2");
    }

    [Fact]
    public async Task DependencyInjection_RegistersBothDispatchers_TheSelector_AndTheRealV2ClientFactory()
    {
        // The strategy's selector and V2 factory are optional ctor params (= null), so a registration that
        // silently stopped resolving would degrade every machine to V1 with every unit test still green.
        await Run<IScriptDispatcherSelector, IEnumerable<IScriptDispatcher>, IScriptServiceV2ClientFactory>((selector, dispatchers, v2Factory) =>
        {
            dispatchers.Select(d => d.Version).OrderBy(v => v).ShouldBe(new[] { ScriptProtocolVersion.V1, ScriptProtocolVersion.V2 });
            selector.ForVersion(ScriptProtocolVersion.V1).ShouldBeOfType<Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers.V1ScriptDispatcher>();
            selector.ForVersion(ScriptProtocolVersion.V2).ShouldBeOfType<Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers.V2ScriptDispatcher>();
            v2Factory.ShouldBeOfType<HalibutClientFactory>();

            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    private async Task RunPipelineAsync(SeededDeployment seeded, Agents agents, bool advertiseV2, TimeSpan timeout)
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        cache.Store(seeded.DispatchSlot.MachineId, new Dictionary<string, string> { ["os"] = "Linux" }, "2.0.0", advertiseV2 ? new[] { "IScriptService/v1", ScriptServiceCapabilities.V2 } : new[] { "IScriptService/v1" });

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
        => Run<IDeploymentCheckpointService, string>(async checkpoints =>
        {
            var checkpoint = await checkpoints.LoadAsync(seeded.TaskId).ConfigureAwait(false);

            return checkpoint?.InFlightScriptsJson;
        });

    private sealed class Agents
    {
        public BlockingV2Agent V2 { get; init; }
        public IntegrationPausePreservesRemoteScript.BlockingAgent V1 { get; init; }
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

        public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => throw new NotSupportedException();

        public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint) => throw new NotSupportedException();
    }

    // Script stays Running until Finish(); LoseResult() turns every known ticket into ResultLost (the agent restarted mid-run).
    private sealed class BlockingV2Agent : IAsyncScriptServiceV2
    {
        private volatile bool _finished;
        private volatile bool _lost;
        private int _cancelCalls;
        private readonly HashSet<string> _known = new();

        public int OpenedClients { get; set; }
        public int CancelCalls => _cancelCalls;
        public List<string> StartedTickets { get; } = new();
        public List<string> ProbedTickets { get; } = new();

        public void Finish() => _finished = true;

        public void LoseResult() => _lost = true;

        public Task<ScriptStatusResponseV2> StartScriptAsync(StartScriptCommandV2 command)
        {
            lock (_known)
            {
                if (_known.Add(command.ScriptTicket.TaskId)) StartedTickets.Add(command.ScriptTicket.TaskId);
            }

            return Task.FromResult(Resp(command.ScriptTicket, ScriptStateV2.Running));
        }

        public Task<ScriptStatusResponseV2> GetStatusAsync(ScriptStatusRequestV2 request)
        {
            lock (ProbedTickets) ProbedTickets.Add(request.Ticket.TaskId);

            bool known;
            lock (_known) known = _known.Contains(request.Ticket.TaskId);

            if (!known) return Task.FromResult(Resp(request.Ticket, ScriptStateV2.UnknownTicket));
            if (_lost) return Task.FromResult(Resp(request.Ticket, ScriptStateV2.ResultLost));

            return Task.FromResult(Resp(request.Ticket, _finished ? ScriptStateV2.Completed : ScriptStateV2.Running));
        }

        public Task<ScriptStatusResponseV2> CancelScriptAsync(CancelScriptCommandV2 command)
        {
            Interlocked.Increment(ref _cancelCalls);

            return Task.FromResult(Resp(command.Ticket, ScriptStateV2.Completed));
        }

        public Task<CompleteScriptResponseV2> CompleteScriptAsync(CompleteScriptCommandV2 command)
            => Task.FromResult(new CompleteScriptResponseV2(command.Ticket, CompleteScriptOutcomeV2.Completed, Resp(command.Ticket, ScriptStateV2.Completed)));

        private static ScriptStatusResponseV2 Resp(ScriptTicket ticket, ScriptStateV2 state) => new(ticket, state, 0, new List<ProcessOutput>(), 0);
    }
}
