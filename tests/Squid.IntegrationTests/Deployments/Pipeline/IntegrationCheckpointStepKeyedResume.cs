using Autofac;
using Halibut;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Enums;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Real pipeline + real Postgres checkpoint + real guided-failure interruption; only the agent RPC is faked.
/// Two steps of ONE parallel batch (S2 is StartWithPrevious) target the SAME machine. S1 succeeds, S2 fails
/// into a guided-failure pause. The checkpoint records completion per (step, machine), so after the operator
/// chooses Retry only S2 re-runs on the machine. Keyed by machine alone, S1 finishing there made S2 look done
/// and the retry was skipped while the deployment reported success.
/// </summary>
public class IntegrationCheckpointStepKeyedResume : DeploymentFixtureBase
{
    private const string Step2Marker = "step-two-marker";

    [Fact]
    public async Task ParallelBatch_SameMachine_Step2FailsGuided_Retry_RerunsOnlyStep2()
    {
        var (seeded, step2Id) = await SeedTwoStepParallelGuidedAsync().ConfigureAwait(false);
        var agent = new TwoStepAgent();

        await RunPipelineAsync(seeded.TaskId, agent).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Paused).ConfigureAwait(false);
        agent.Starts(Step2Marker).ShouldBe(1);
        agent.StartsOfStep1.ShouldBe(1);

        var checkpoint = await LoadCheckpointAsync(seeded.TaskId).ConfigureAwait(false);
        var persisted = DeserializeBatch0(checkpoint.BatchStatesJson);
        persisted.IsTerminalFor(seeded.DispatchSlot.StepId, seeded.DispatchSlot.MachineId).ShouldBeTrue("step 1 finished on the machine before the pause");
        persisted.IsTerminalFor(step2Id, seeded.DispatchSlot.MachineId).ShouldBeFalse("step 2 failed into guided failure — it must stay runnable");

        await SubmitRetryAsync(seeded.TaskId).ConfigureAwait(false);
        agent.Step2Succeeds = true;
        await RunPipelineAsync(seeded.TaskId, agent, resume: true).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
        agent.Starts(Step2Marker).ShouldBe(2, "the retried step 2 must actually run again on the machine");
        agent.StartsOfStep1.ShouldBe(1, "step 1 already completed — resume must not re-run it");
    }

    [Fact]
    public async Task LegacyMachineLevelCheckpoint_ResumeTreatsEveryStepOfBatchAsDone()
    {
        // A checkpoint written by a server that predates step-keying: machine id only. It meant "this machine
        // is finished for the batch", so after upgrade neither step may be re-dispatched to it.
        var (seeded, _) = await SeedTwoStepParallelGuidedAsync().ConfigureAwait(false);
        var agent = new TwoStepAgent();

        await RunPipelineAsync(seeded.TaskId, agent).ConfigureAwait(false);
        await AssertStateAsync(seeded.TaskId, TaskState.Paused).ConfigureAwait(false);

        await OverwriteBatchStatesAsync(seeded.TaskId, $"{{\"0\":{{\"CompletedMachineIds\":[{seeded.DispatchSlot.MachineId}],\"FailedMachineIds\":[]}}}}").ConfigureAwait(false);
        await SubmitRetryAsync(seeded.TaskId).ConfigureAwait(false);
        var startsBeforeResume = agent.TotalStarts;

        await RunPipelineAsync(seeded.TaskId, agent, resume: true).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
        agent.TotalStarts.ShouldBe(startsBeforeResume, "legacy machine-level entry covers every step of the batch on that machine, so nothing re-runs");
    }

    private async Task<(SeededDeployment Seeded, int Step2Id)> SeedTwoStepParallelGuidedAsync()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var step2Id = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);
            var step1 = await repository.Query<DeploymentStep>(s => s.Id == seeded.DispatchSlot.StepId).FirstAsync().ConfigureAwait(false);

            var step2 = await builder.CreateDeploymentStepAsync(step1.ProcessId, 2, "Run script two", isRequired: true).ConfigureAwait(false);
            step2.StartTrigger = "StartWithPrevious";
            await repository.UpdateAsync(step2).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            await builder.CreateStepPropertiesAsync(step2.Id, ("Squid.Action.TargetRoles", "web")).ConfigureAwait(false);
            var action = await builder.CreateDeploymentActionAsync(step2.Id, 1, "Run script two", actionType: SpecialVariables.ActionTypes.Script).ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action.Id, "web").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id, ("Squid.Action.Script.ScriptBody", $"echo {Step2Marker}"), ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            var task = await repository.QueryNoTracking<ServerTask>(t => t.Id == seeded.TaskId).FirstAsync().ConfigureAwait(false);
            var environment = await repository.Query<Environment>(e => e.Id == task.EnvironmentId).FirstAsync().ConfigureAwait(false);
            environment.UseGuidedFailure = true;
            await repository.UpdateAsync(environment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            step2Id = step2.Id;
        }).ConfigureAwait(false);

        return (seeded, step2Id);
    }

    private static BatchCheckpointState DeserializeBatch0(string json)
        => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, BatchCheckpointState>>(json)["0"];

    private Task<DeploymentExecutionCheckpoint> LoadCheckpointAsync(int taskId)
        => Run<IDeploymentCheckpointService, DeploymentExecutionCheckpoint>(service => service.LoadAsync(taskId));

    private Task OverwriteBatchStatesAsync(int taskId, string json)
        => Run<IDeploymentCheckpointService>(async service =>
        {
            var checkpoint = await service.LoadAsync(taskId).ConfigureAwait(false);
            checkpoint.BatchStatesJson = json;

            await service.SaveAsync(checkpoint).ConfigureAwait(false);
        });

    private Task SubmitRetryAsync(int taskId)
        => Run<IDeploymentInterruptionService>(async service =>
        {
            var pending = await service.GetPendingInterruptionsAsync(taskId, CancellationToken.None).ConfigureAwait(false);

            pending.Count.ShouldBe(1, "exactly one guided-failure prompt (step 2 on the machine)");

            await service.SubmitInterruptionAsync(pending[0].Id, new Dictionary<string, string> { ["Result"] = "Retry" }, CancellationToken.None).ConfigureAwait(false);
        });

    private Task AssertStateAsync(int taskId, string expected)
        => Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(taskId, CancellationToken.None).ConfigureAwait(false);

            task.State.ShouldBe(expected);
        });

    private Task RunPipelineAsync(int taskId, IAsyncScriptService agent, bool resume = false)
        => Run<ILifetimeScope>(async scope =>
        {
            var runner = new DeploymentPipelineRunner(
                scope.Resolve<IEnumerable<IDeploymentPipelinePhase>>(),
                scope.Resolve<IDeploymentLifecycle>(),
                scope.Resolve<IDeploymentCompletionHandler>(),
                scope.Resolve<ITaskCancellationRegistry>(),
                scope.Resolve<IServerTaskDataProvider>(),
                scope.Resolve<ISquidBackgroundJobClient>())
            {
                DeploymentTimeout = TimeSpan.FromSeconds(60),
                TimeoutResumable = true
            };

            await runner.ProcessAsync(taskId, CancellationToken.None, resume).WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        }, extraRegistration: b => b.RegisterInstance(new FixedClientFactory(agent)).As<IHalibutClientFactory>().SingleInstance());

    // Step 1 (any script without the marker) always succeeds. Step 2 fails its first run, but only after step 1's
    // script has been completed AND the target has had time to record it, so the guided-failure checkpoint write
    // deterministically sees step 1 done. Later step-2 runs succeed once Step2Succeeds is set.
    private sealed class TwoStepAgent : IAsyncScriptService
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, bool> _isStep2ByTicket = new();
        private readonly TaskCompletionSource _step1Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _step2Starts;

        public bool Step2Succeeds { get; set; }
        public int StartsOfStep1 { get; private set; }
        public int TotalStarts => StartsOfStep1 + _step2Starts;

        public int Starts(string marker) => marker == Step2Marker ? _step2Starts : throw new ArgumentException(marker);

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command)
        {
            lock (_gate)
            {
                var isStep2 = command.ScriptBody.Contains(Step2Marker, StringComparison.Ordinal);
                _isStep2ByTicket[command.ScriptTicket.TaskId] = isStep2;

                if (isStep2) _step2Starts++;
                else StartsOfStep1++;
            }

            return Task.FromResult(Resp(command.ScriptTicket, ProcessState.Running, 0));
        }

        public async Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
        {
            if (!IsStep2(request.Ticket)) return Resp(request.Ticket, ProcessState.Complete, 0);

            if (!Step2Succeeds)
            {
                await _step1Completed.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }

            return Resp(request.Ticket, ProcessState.Complete, Step2Succeeds ? 0 : 1);
        }

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command)
        {
            if (!IsStep2(command.Ticket)) _step1Completed.TrySetResult();

            return Task.FromResult(Resp(command.Ticket, ProcessState.Complete, IsStep2(command.Ticket) && !Step2Succeeds ? 1 : 0));
        }

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command) => Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 0));

        private bool IsStep2(ScriptTicket ticket)
        {
            lock (_gate) return _isStep2ByTicket[ticket.TaskId];
        }

        private static ScriptStatusResponse Resp(ScriptTicket ticket, ProcessState state, int exitCode) => new(ticket, state, exitCode, new List<ProcessOutput>(), 0);
    }

    private sealed class FixedClientFactory : IHalibutClientFactory
    {
        private readonly IAsyncScriptService _client;

        public FixedClientFactory(IAsyncScriptService client) => _client = client;

        public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => _client;

        public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => new NoOpCapabilities();

        public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint) => throw new NotSupportedException();

        private sealed class NoOpCapabilities : IAsyncCapabilitiesService
        {
            public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request) => Task.FromResult(new CapabilitiesResponse());
        }
    }
}
