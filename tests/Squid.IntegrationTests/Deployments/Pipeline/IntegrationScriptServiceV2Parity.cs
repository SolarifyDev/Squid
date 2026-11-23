using System.Diagnostics;
using Autofac;
using Halibut;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
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
/// The V2 observation path through the real pipeline + real strategy / dispatchers / observer + real Postgres checkpoint, against a
/// scripted V2 agent: a CompleteScript that cannot be confirmed after GetStatus saw the script finish must not pause the deployment
/// (V1 never did), and an agent that has gone away while it still answered "not yet complete" must be caught by the liveness probe
/// rather than by the deployment timeout.
/// </summary>
public class IntegrationScriptServiceV2Parity : DeploymentFixtureBase
{
    private static readonly HalibutSetting FastRetries = new() { RpcRetry = new RpcRetrySettings { BudgetSeconds = 1, CancelBudgetSeconds = 1, CompleteBudgetSeconds = 1 } };

    [Fact]
    public async Task V2_CompleteCannotBeConfirmed_AfterTheScriptFinished_TheDeploymentSucceeds_InsteadOfPausing()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new ScriptedV2Agent { CompleteFails = true };

        await RunPipelineAsync(seeded, agent, observer: null, timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
        agent.StartedTickets.Count.ShouldBe(1);
        agent.CompleteCalls.ShouldBeGreaterThan(1, "the Complete was retried within its budget before it was given up on");
    }

    [Fact]
    public async Task V2_AgentGoesAwayWhileCompleteSaysNotYetComplete_TheLivenessProbeCatchesIt_NotTheDeploymentTimeout()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new ScriptedV2Agent { CompleteSaysNotYet = true };
        var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 50, MaxPollIntervalMs = 100 }, new LivenessSettings { ProbeIntervalSeconds = 1, ProbeTimeoutSeconds = 1, FailureThreshold = 1 }, new DeadProbe());
        var clock = Stopwatch.StartNew();

        await RunPipelineAsync(seeded, agent, observer, timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), "an unreachable agent pauses the deployment within a few probes, not after the 60s deployment timeout");
        await AssertStateAsync(seeded.TaskId, TaskState.Paused).ConfigureAwait(false);
        agent.CancelCalls.ShouldBe(0, "a pause for an unreachable agent must not send CancelScript: the script must survive for the resume");
    }

    private async Task RunPipelineAsync(SeededDeployment seeded, ScriptedV2Agent agent, IHalibutScriptObserver observer, TimeSpan timeout)
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        cache.Store(seeded.DispatchSlot.MachineId, new Dictionary<string, string> { ["os"] = "Linux" }, "2.0.0", new[] { "IScriptService/v1", ScriptServiceCapabilities.V2 });

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
            b.RegisterInstance(FastRetries).AsSelf().SingleInstance();
            b.RegisterInstance(new Factories(agent)).As<IHalibutClientFactory>().As<IScriptServiceV2ClientFactory>().SingleInstance();

            if (observer != null) b.RegisterInstance(observer).As<IHalibutScriptObserver>().SingleInstance();
        }).ConfigureAwait(false);
    }

    private Task AssertStateAsync(int taskId, string expected)
        => Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(taskId, CancellationToken.None).ConfigureAwait(false);

            task.State.ShouldBe(expected);
        });

    private sealed class DeadProbe : IAgentLivenessProbe
    {
        public Task<bool> ProbeAsync(ServiceEndPoint endpoint, TimeSpan timeout, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class Factories : IHalibutClientFactory, IScriptServiceV2ClientFactory
    {
        private readonly ScriptedV2Agent _agent;

        public Factories(ScriptedV2Agent agent) => _agent = agent;

        public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => throw new NotSupportedException("a V2 dispatch must not open the V1 service");

        public IAsyncScriptServiceV2 CreateScriptServiceV2Client(ServiceEndPoint endpoint) => _agent;

        public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => throw new NotSupportedException();

        public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint) => throw new NotSupportedException();
    }

    // CompleteFails: GetStatus reports Completed, every CompleteScript fails like a dropped connection.
    // CompleteSaysNotYet: the first GetStatus reports Completed, CompleteScript answers "not yet complete" (Running) and every later GetStatus says Running.
    private sealed class ScriptedV2Agent : IAsyncScriptServiceV2
    {
        private int _polls;
        private int _completeCalls;
        private int _cancelCalls;

        public bool CompleteFails { get; init; }
        public bool CompleteSaysNotYet { get; init; }
        public int CompleteCalls => _completeCalls;
        public int CancelCalls => _cancelCalls;
        public List<string> StartedTickets { get; } = new();

        public Task<ScriptStatusResponseV2> StartScriptAsync(StartScriptCommandV2 command)
        {
            lock (StartedTickets) StartedTickets.Add(command.ScriptTicket.TaskId);

            return Task.FromResult(Resp(command.ScriptTicket, ScriptStateV2.Running));
        }

        public Task<ScriptStatusResponseV2> GetStatusAsync(ScriptStatusRequestV2 request)
        {
            var first = Interlocked.Increment(ref _polls) == 1;

            return Task.FromResult(Resp(request.Ticket, CompleteSaysNotYet && !first ? ScriptStateV2.Running : ScriptStateV2.Completed));
        }

        public Task<ScriptStatusResponseV2> CancelScriptAsync(CancelScriptCommandV2 command)
        {
            Interlocked.Increment(ref _cancelCalls);

            return Task.FromResult(Resp(command.Ticket, ScriptStateV2.Completed));
        }

        public Task<CompleteScriptResponseV2> CompleteScriptAsync(CompleteScriptCommandV2 command)
        {
            Interlocked.Increment(ref _completeCalls);

            if (CompleteFails) throw new HalibutClientException("connection reset");

            return Task.FromResult(CompleteSaysNotYet
                ? new CompleteScriptResponseV2(command.Ticket, CompleteScriptOutcomeV2.NotYetComplete, Resp(command.Ticket, ScriptStateV2.Running))
                : new CompleteScriptResponseV2(command.Ticket, CompleteScriptOutcomeV2.Completed, Resp(command.Ticket, ScriptStateV2.Completed)));
        }

        private static ScriptStatusResponseV2 Resp(ScriptTicket ticket, ScriptStateV2 state) => new(ticket, state, 0, new List<ProcessOutput>(), 0);
    }
}
