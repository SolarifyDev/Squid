using Halibut;
using Halibut.Exceptions;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Settings.Halibut;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Enums;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Pins the agent-RPC retry semantic through the REAL pipeline (real runner, phase, strategy, observer and
/// in-flight store on Postgres; only the agent RPC is faked), with the budget supplied through <see cref="RpcRetrySettings"/>
/// (production binds it from <c>Halibut:RpcRetry</c>). A GetStatus failure that recovers inside the budget must not pause the deployment; one that
/// outlasts the budget must pause it with the in-flight pointer preserved for resume.
/// Fidelity: medium-mock (real production classes, faked agent transport), consistent with the sibling
/// transient-pause tests.
/// </summary>
public class IntegrationRpcRetryBudget : DeploymentFixtureBase
{
    [Fact]
    public async Task GetStatusBlipRecoveringWithinBudget_DoesNotPause_AndTaskSucceeds()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new FlakyAgent(failuresBeforeRecovery: 2);

        await ProcessAsync(seeded.TaskId, agent, budget: TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        await Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(seeded.TaskId, CancellationToken.None).ConfigureAwait(false);

            task.State.ShouldBe(TaskState.Success, "a connection blip shorter than the retry budget must be absorbed, not paused");
        }).ConfigureAwait(false);

        agent.GetStatusCalls.ShouldBe(3, "two failed attempts then the successful poll");
        agent.StartCalls.ShouldBe(1, "a blip must never cause a duplicate dispatch");
    }

    [Fact]
    public async Task GetStatusOutageOutlastingBudget_Pauses_AndPreservesTheInFlightPointer()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new FlakyAgent(failuresBeforeRecovery: int.MaxValue);

        await ProcessAsync(seeded.TaskId, agent, budget: TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        await Run<IServerTaskDataProvider, IDeploymentCheckpointService>(async (provider, checkpoints) =>
        {
            var task = await provider.GetServerTaskByIdAsync(seeded.TaskId, CancellationToken.None).ConfigureAwait(false);
            task.State.ShouldBe(TaskState.Paused, "an outage that outlasts the retry budget keeps the pre-retry outcome: pause for resume");

            var checkpoint = await checkpoints.LoadAsync(seeded.TaskId).ConfigureAwait(false);
            checkpoint.ShouldNotBeNull();
            InFlightScriptMap.TryGet(checkpoint.InFlightScriptsJson, seeded.DispatchSlot).ShouldBe(agent.StartedTicket, "resume must re-attach to the exact ticket that was dispatched");
        }).ConfigureAwait(false);

        agent.GetStatusCalls.ShouldBeGreaterThan(1, "it retried before pausing");
        agent.CancelCalls.ShouldBe(0, "a pause must leave the script running");
    }

    private Task ProcessAsync(int taskId, FlakyAgent agent, TimeSpan budget)
        => Run<IDeploymentTaskExecutor>(
            executor => executor.ProcessAsync(taskId, CancellationToken.None),
            extraRegistration: b =>
            {
                b.RegisterInstance(new FixedFactory(agent)).As<IHalibutClientFactory>().SingleInstance();
                b.RegisterInstance(new RpcRetryHandler(new RpcRetrySettings { BudgetSeconds = (int)budget.TotalSeconds, CancelBudgetSeconds = (int)budget.TotalSeconds, CompleteBudgetSeconds = (int)budget.TotalSeconds })).AsSelf().SingleInstance();
            });

    private sealed class FlakyAgent : IAsyncScriptService
    {
        private readonly int _failures;
        private int _start, _status, _cancel;

        public FlakyAgent(int failuresBeforeRecovery) => _failures = failuresBeforeRecovery;

        public string StartedTicket { get; private set; }
        public int StartCalls => _start;
        public int GetStatusCalls => _status;
        public int CancelCalls => _cancel;

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command)
        {
            Interlocked.Increment(ref _start);
            StartedTicket = command.ScriptTicket.TaskId;

            return Task.FromResult(Resp(command.ScriptTicket, ProcessState.Running));
        }

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
        {
            if (Interlocked.Increment(ref _status) <= _failures) throw new HalibutClientException("transient: connection reset");

            return Task.FromResult(Resp(request.Ticket, ProcessState.Complete));
        }

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) => Task.FromResult(Resp(command.Ticket, ProcessState.Complete));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command)
        {
            Interlocked.Increment(ref _cancel);

            return Task.FromResult(Resp(command.Ticket, ProcessState.Complete));
        }

        private static ScriptStatusResponse Resp(ScriptTicket ticket, ProcessState state) => new(ticket, state, 0, new List<ProcessOutput>(), 0);
    }

    private sealed class FixedFactory : IHalibutClientFactory
    {
        private readonly IAsyncScriptService _client;

        public FixedFactory(IAsyncScriptService client) => _client = client;

        public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => _client;

        public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => new NoOpCapabilities();

        public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint) => throw new NotSupportedException();

        private sealed class NoOpCapabilities : IAsyncCapabilitiesService
        {
            public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request) => Task.FromResult(new CapabilitiesResponse());
        }
    }
}
