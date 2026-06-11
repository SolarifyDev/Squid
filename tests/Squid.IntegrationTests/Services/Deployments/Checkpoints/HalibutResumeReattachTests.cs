using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Halibut;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.IntegrationTests.Base;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;
using Machine = Squid.Core.Persistence.Entities.Deployments.Machine;

namespace Squid.IntegrationTests.Services.Deployments.Checkpoints;

/// <summary>
/// Resume-by-ticket — integration coverage for the crash-resume RE-ATTACH wiring in
/// <see cref="HalibutMachineExecutionStrategy"/>. The unit tier
/// (<c>HalibutReattachDecisionTests</c>) pins the <c>AgentHasUsableScript</c> decision and
/// <c>InFlightScriptStoreTests</c> pins the DB-backed store; what neither covers is the
/// end-to-end wiring: a ticket persisted by a prior (crashed) run is read back by a FRESH
/// strategy instance, the agent is probed with that SAME ticket, and the script is observed
/// to completion WITHOUT a duplicate <c>StartScript</c>.
///
/// <para><b>Why this gap mattered</b>: the full-pipeline resume E2E
/// (<c>KubernetesResumeCheckpointE2ETests</c>) swaps in <c>CapturingExecutionStrategy</c>,
/// which replaces <see cref="HalibutMachineExecutionStrategy"/> entirely — so
/// <c>DispatchOrReattachAsync</c>/<c>TryReattachAsync</c> were never exercised by any
/// pipeline test. This drives the REAL strategy + REAL observer + REAL DB-backed
/// <see cref="IInFlightScriptStore"/>, mocking only the Halibut RPC transport (the agent),
/// whose unknown-ticket contract is itself unit-pinned.</para>
///
/// <para><b>Tier</b>: integration (Rule 9) — real Postgres checkpoint row carries the ticket
/// across a simulated restart (the strategy resolves a fresh store from a new DI scope that
/// reads the same DB). The agent is a recording fake injected via a per-scope
/// <see cref="IHalibutClientFactory"/> override (Rule 12 medium-mock: real prod class, mocked
/// external dep covered elsewhere).</para>
/// </summary>
public class HalibutResumeReattachTests : TestBase
{
    public HalibutResumeReattachTests() : base("HalibutResumeReattach", "squid_it_reattach")
    {
    }

    [Fact]
    public async Task RecordedTicket_AgentHasCompleteScript_ReattachesWithoutDuplicateDispatch()
    {
        const int taskId = 810001, machineId = 11;
        const string ticket = "reattach-ticket-complete";

        await EnsureRowAsync(taskId).ConfigureAwait(false);
        await RecordTicketAsync(taskId, machineId, ticket).ConfigureAwait(false);

        var agent = new RecordingScriptService(
            getStatusScript: new[] { (ProcessState.Complete, 0) },
            completeResult: (ProcessState.Complete, 0));

        var result = await ExecuteWithAgentAsync(taskId, machineId, agent).ConfigureAwait(false);

        agent.StartScriptCalls.ShouldBe(0,
            customMessage: "Re-attach MUST NOT dispatch a duplicate StartScript when the agent still holds the recorded ticket. " +
                          "A non-zero count means the crashed run's script and a fresh dispatch both execute (the exact double-run this prevents).");
        agent.ProbedTickets.ShouldContain(ticket);
        result.Success.ShouldBeTrue();
        result.ExitCode.ShouldBe(0);

        (await GetTicketAsync(taskId, machineId).ConfigureAwait(false)).ShouldBeNull(
            customMessage: "In-flight ticket MUST be cleared once the re-attached script completes, or the next run re-probes a dead ticket.");
    }

    [Fact]
    public async Task RecordedTicket_AgentScriptStillRunning_ReattachesAndObservesToCompletion()
    {
        const int taskId = 810002, machineId = 12;
        const string ticket = "reattach-ticket-running";

        await EnsureRowAsync(taskId).ConfigureAwait(false);
        await RecordTicketAsync(taskId, machineId, ticket).ConfigureAwait(false);

        // Probe sees the script still Running; the observer's next poll sees it Complete.
        var agent = new RecordingScriptService(
            getStatusScript: new[] { (ProcessState.Running, 0), (ProcessState.Complete, 0) },
            completeResult: (ProcessState.Complete, 0));

        var result = await ExecuteWithAgentAsync(taskId, machineId, agent).ConfigureAwait(false);

        agent.StartScriptCalls.ShouldBe(0,
            customMessage: "A still-running script from a crashed run must be re-attached to, not re-dispatched.");
        agent.ProbedTickets.ShouldAllBe(t => t == ticket,
            customMessage: "Every status probe (initial + observer polls) must target the recorded ticket, never a fresh one.");
        result.Success.ShouldBeTrue();

        (await GetTicketAsync(taskId, machineId).ConfigureAwait(false)).ShouldBeNull();
    }

    [Fact]
    public async Task RecordedTicket_AgentNoLongerHasScript_ClearsStaleAndDispatchesFresh()
    {
        const int taskId = 810003, machineId = 13;
        const string staleTicket = "stale-ticket";

        await EnsureRowAsync(taskId).ConfigureAwait(false);
        await RecordTicketAsync(taskId, machineId, staleTicket).ConfigureAwait(false);

        // The Tentacle returns Complete + UnknownResult(-1) for a ticket it doesn't have —
        // the single "agent doesn't hold it" signal that must fall back to a fresh dispatch.
        var agent = new RecordingScriptService(
            getStatusScript: new[] { (ProcessState.Complete, ScriptExitCodes.UnknownResult), (ProcessState.Complete, 0) },
            completeResult: (ProcessState.Complete, 0));

        var result = await ExecuteWithAgentAsync(taskId, machineId, agent).ConfigureAwait(false);

        agent.StartScriptCalls.ShouldBe(1,
            customMessage: "A stale ticket (agent returns Complete+UnknownResult) MUST fall back to exactly one fresh dispatch.");
        agent.StartedTickets.Single().ShouldNotBe(staleTicket,
            customMessage: "The fresh dispatch must use a new ticket, not re-send the stale one.");
        result.Success.ShouldBeTrue();

        (await GetTicketAsync(taskId, machineId).ConfigureAwait(false)).ShouldBeNull();
    }

    [Fact]
    public async Task NoRecordedTicket_DispatchesFreshWithoutReattach()
    {
        const int taskId = 810004, machineId = 14;

        // Checkpoint row exists (today's executor creates it before dispatch) but no
        // in-flight ticket was recorded — there is nothing to re-attach to.
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        var agent = new RecordingScriptService(
            getStatusScript: new[] { (ProcessState.Complete, 0) },
            completeResult: (ProcessState.Complete, 0));

        var result = await ExecuteWithAgentAsync(taskId, machineId, agent).ConfigureAwait(false);

        agent.StartScriptCalls.ShouldBe(1,
            customMessage: "With no recorded ticket the strategy must dispatch fresh — re-attach can only ever AVOID a duplicate.");
        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task FreshDispatch_RecordsInFlightTicket_BeforeStartScriptRpc()
    {
        // The record-before-RPC guarantee (P1, server-side StartScript idempotency):
        // the in-flight ticket MUST be durably persisted BEFORE the StartScript RPC
        // fires. Otherwise a crash / lost response in the window between the agent
        // launching the script and the server recording the ticket leaves no pointer,
        // and the resumed run re-dispatches a fresh ticket → the script runs twice.
        const int taskId = 810005, machineId = 15;

        await EnsureRowAsync(taskId).ConfigureAwait(false);

        // Capture, at the instant StartScript fires, what the store has COMMITTED —
        // read through a fresh DI scope, exactly as a resumed server would see it.
        string recordedWhenStartScriptFired = null;

        var agent = new RecordingScriptService(
            getStatusScript: new[] { (ProcessState.Complete, 0) },
            completeResult: (ProcessState.Complete, 0))
        {
            OnStartScript = async _ => recordedWhenStartScriptFired = await GetTicketAsync(taskId, machineId).ConfigureAwait(false)
        };

        var result = await ExecuteWithAgentAsync(taskId, machineId, agent).ConfigureAwait(false);

        result.Success.ShouldBeTrue();
        agent.StartScriptCalls.ShouldBe(1);

        recordedWhenStartScriptFired.ShouldBe(agent.StartedTickets.Single(),
            customMessage: "record-before-RPC violated: the in-flight ticket must be durably persisted BEFORE StartScript fires. " +
                          "If it is only recorded after the RPC returns, a crash / lost response in that window leaves no pointer " +
                          "and the resumed run re-dispatches a duplicate (the exact double-run this ordering prevents).");
    }

    // ── Drive the real strategy with a fake agent (per-scope IHalibutClientFactory override) ──

    private async Task<ScriptExecutionResult> ExecuteWithAgentAsync(int taskId, int machineId, RecordingScriptService agent)
    {
        ScriptExecutionResult result = null;

        await Run<IEnumerable<IExecutionStrategy>>(
            async strategies =>
            {
                var strategy = strategies.OfType<HalibutMachineExecutionStrategy>().Single();
                result = await strategy.ExecuteScriptAsync(BuildRequest(taskId, machineId), CancellationToken.None).ConfigureAwait(false);
            },
            extraRegistration: b => b.RegisterInstance(new FixedHalibutClientFactory(agent)).As<IHalibutClientFactory>()).ConfigureAwait(false);

        return result;
    }

    private static ScriptExecutionRequest BuildRequest(int taskId, int machineId) => new()
    {
        ExecutionMode = ExecutionMode.DirectScript,
        ScriptBody = "echo resume-reattach",
        Syntax = ScriptSyntax.Bash,
        Variables = new List<VariableDto>(),
        ServerTaskId = taskId,
        StepName = "Step1",
        ActionName = "Action1",
        Machine = new Machine
        {
            Id = machineId,
            Name = $"agent-{machineId}",
            Endpoint = JsonSerializer.Serialize(new
            {
                CommunicationStyle = "KubernetesAgent",
                SubscriptionId = $"sub-{machineId}",
                Thumbprint = "AA11BB22CC33DD44"
            })
        }
    };

    // ── Helpers (mirror InFlightScriptStoreTests) ──

    private Task EnsureRowAsync(int taskId)
        => Run<IDeploymentCheckpointService>(svc => svc.EnsureExistsAsync(taskId, deploymentId: 1));

    private Task RecordTicketAsync(int taskId, int machineId, string ticket)
        => Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, machineId, ticket));

    private Task<string> GetTicketAsync(int taskId, int machineId)
        => Run<IInFlightScriptStore, string>(s => s.TryGetTicketAsync(taskId, machineId));

    // ── Recording fake agent (the Halibut RPC counterpart) ──

    private sealed class RecordingScriptService : IAsyncScriptService
    {
        private readonly Queue<(ProcessState State, int ExitCode)> _getStatusScript;
        private readonly (ProcessState State, int ExitCode) _completeResult;

        public RecordingScriptService(IEnumerable<(ProcessState, int)> getStatusScript, (ProcessState, int) completeResult)
        {
            _getStatusScript = new Queue<(ProcessState, int)>(getStatusScript);
            _completeResult = completeResult;
        }

        public int StartScriptCalls { get; private set; }
        public List<string> StartedTickets { get; } = new();
        public List<string> ProbedTickets { get; } = new();

        /// <summary>Hook invoked WHILE StartScript is executing on the agent — lets a test
        /// observe server-side state (e.g. the durably-recorded in-flight ticket) at the exact
        /// instant the dispatch RPC is in flight.</summary>
        public Func<StartScriptCommand, Task> OnStartScript { get; set; }

        public async Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command)
        {
            StartScriptCalls++;
            StartedTickets.Add(command.ScriptTicket.TaskId);

            if (OnStartScript != null)
                await OnStartScript(command).ConfigureAwait(false);

            return Resp(command.ScriptTicket, ProcessState.Running, 0);
        }

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
        {
            ProbedTickets.Add(request.Ticket.TaskId);
            // Last scripted entry repeats so the observer's poll loop always terminates.
            var (state, exit) = _getStatusScript.Count > 1 ? _getStatusScript.Dequeue() : _getStatusScript.Peek();
            return Task.FromResult(Resp(request.Ticket, state, exit));
        }

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command)
            => Task.FromResult(Resp(command.Ticket, _completeResult.State, _completeResult.ExitCode));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command)
            => Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 0));

        private static ScriptStatusResponse Resp(ScriptTicket ticket, ProcessState state, int exitCode)
            => new(ticket, state, exitCode, new List<ProcessOutput>(), 0);
    }

    private sealed class FixedHalibutClientFactory : IHalibutClientFactory
    {
        private readonly IAsyncScriptService _client;

        public FixedHalibutClientFactory(IAsyncScriptService client) => _client = client;

        public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => _client;

        public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => new NoOpCapabilitiesService();

        public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint) => new ThrowingFileTransferService();

        private sealed class NoOpCapabilitiesService : IAsyncCapabilitiesService
        {
            public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request)
                => Task.FromResult(new CapabilitiesResponse());
        }

        private sealed class ThrowingFileTransferService : IAsyncClientFileTransferService
        {
            public Task<UploadResult> UploadFileAsync(string remotePath, DataStream upload)
                => throw new NotSupportedException("Re-attach tests do not exercise file transfer.");

            public Task<DataStream> DownloadFileAsync(string remotePath)
                => throw new NotSupportedException("Re-attach tests do not exercise file transfer.");
        }
    }
}
