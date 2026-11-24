using Autofac;
using Halibut;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
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
using Squid.Message.Enums.Deployments;
using static Squid.IntegrationTests.Deployments.Pipeline.IntegrationPausePreservesRemoteScript;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Fidelity: HIGH (real Postgres, the real <c>DeploymentPipelineRunner</c> with every real phase, real <c>ExecuteStepsPhase</c> / Halibut strategy / observer,
/// real lease keeper and interruption service; only the agent RPC is faked). Parallel steps share one scoped DbContext, so this relies on the scoped database
/// gate that serialises their database access. Two steps of ONE parallel batch: step 1's script keeps running on its agent while step 2, a manual intervention, suspends the
/// task.
/// </summary>
public class IntegrationSuspendInParallelBatch : DeploymentFixtureBase
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // A suspend (guided failure / manual intervention) moves the task to Paused and must stop the batch's siblings with a PAUSE decision: nothing may keep
    // running in the background of a Paused task, because the resume that follows would then run beside it. Their agent scripts are left running (pointer kept).
    [Fact]
    public async Task SuspendInsideAParallelBatch_StopsTheSiblings_LeavesTheirScriptsRunning_AndResumeCompletesExactlyOnce()
    {
        var seeded = await SeedScriptWithManualInterventionSiblingAsync().ConfigureAwait(false);
        var agent = new BlockingAgent();

        await Run<ILifetimeScope>(async scope =>
        {
            await using var podA = scope.BeginLifetimeScope();
            var runA = BuildRunner(podA).ProcessAsync(seeded.TaskId, CancellationToken.None);

            await WaitForStateAsync(seeded.TaskId, TaskState.Paused).ConfigureAwait(false);

            await runA.WaitAsync(Bound).ConfigureAwait(false);

            agent.CancelCalls.ShouldBe(0, "a pause leaves the sibling's script running on its agent");
            (await InFlightTicketAsync(seeded).ConfigureAwait(false)).ShouldBe(agent.StartedTickets.Single(), "the sibling's in-flight pointer survives the pause");

            await ResolveInterruptionAsync(seeded.TaskId).ConfigureAwait(false);

            await using var podB = scope.BeginLifetimeScope();
            var runB = BuildRunner(podB).ProcessAsync(seeded.TaskId, CancellationToken.None, resume: true);

            await WaitForStateAsync(seeded.TaskId, TaskState.Executing).ConfigureAwait(false);
            agent.Finish();

            await runB.WaitAsync(Bound).ConfigureAwait(false);
        }, extraRegistration: b => RegisterGatedInterruptions(b, agent)).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
        agent.StartedTickets.Count.ShouldBe(1, "the resume re-attached to the sibling's script instead of dispatching it again");
        agent.CancelCalls.ShouldBe(0);
    }

    private static DeploymentPipelineRunner BuildRunner(ILifetimeScope scope)
        => new(
            scope.Resolve<IEnumerable<IDeploymentPipelinePhase>>(),
            scope.Resolve<IDeploymentLifecycle>(),
            scope.Resolve<IDeploymentCompletionHandler>(),
            scope.Resolve<ITaskCancellationRegistry>(),
            scope.Resolve<IServerTaskDataProvider>(),
            scope.Resolve<ISquidBackgroundJobClient>())
        {
            DeploymentTimeout = TimeSpan.FromSeconds(120),
            TimeoutResumable = true
        };

    // Step 2 (StartWithPrevious, so it shares batch 0 with the script step) is a manual intervention.
    private async Task<SeededDeployment> SeedScriptWithManualInterventionSiblingAsync()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);
            var step1 = await repository.Query<DeploymentStep>(s => s.Id == seeded.DispatchSlot.StepId).FirstAsync().ConfigureAwait(false);

            var step2 = await builder.CreateDeploymentStepAsync(step1.ProcessId, 2, "Approve", isRequired: true).ConfigureAwait(false);
            step2.StartTrigger = "StartWithPrevious";
            await repository.UpdateAsync(step2).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(step2.Id, 1, "Approve", actionType: SpecialVariables.ActionTypes.Manual).ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id, (SpecialVariables.Action.ManualInstructions, "approve the rollout")).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return seeded;
    }

    // The manual intervention suspends only once the sibling's script has been dispatched and polled, so the sibling is provably in flight.
    private static void RegisterGatedInterruptions(ContainerBuilder builder, BlockingAgent agent)
    {
        builder.RegisterInstance(new LocalClientFactory(agent)).As<IHalibutClientFactory>().SingleInstance();
        builder.RegisterInstance(new SiblingInFlight(agent.Polled)).AsSelf().SingleInstance();
        builder.RegisterDecorator<GatedInterruptionService, IDeploymentInterruptionService>();
    }

    private sealed record SiblingInFlight(Task Polled);

    private sealed class GatedInterruptionService(IDeploymentInterruptionService inner, SiblingInFlight sibling) : IDeploymentInterruptionService
    {
        public async Task<DeploymentInterruption> CreateInterruptionAsync(CreateInterruptionRequest request, CancellationToken ct = default)
        {
            await sibling.Polled.WaitAsync(Bound, ct).ConfigureAwait(false);

            return await inner.CreateInterruptionAsync(request, ct).ConfigureAwait(false);
        }

        public Task SubmitInterruptionAsync(int interruptionId, Dictionary<string, string> values, CancellationToken ct = default) => inner.SubmitInterruptionAsync(interruptionId, values, ct);

        public Task TakeResponsibilityAsync(int interruptionId, string userId, CancellationToken ct = default) => inner.TakeResponsibilityAsync(interruptionId, userId, ct);

        public Task<InterruptionOutcome> WaitForInterruptionAsync(int interruptionId, CancellationToken ct) => inner.WaitForInterruptionAsync(interruptionId, ct);

        public Task<DeploymentInterruption> GetInterruptionByIdAsync(int interruptionId, CancellationToken ct = default) => inner.GetInterruptionByIdAsync(interruptionId, ct);

        public Task<List<DeploymentInterruption>> GetPendingInterruptionsAsync(int serverTaskId, CancellationToken ct = default) => inner.GetPendingInterruptionsAsync(serverTaskId, ct);

        public Task<DeploymentInterruption> FindResolvedInterruptionAsync(int serverTaskId, string stepName, string actionName, string machineName, CancellationToken ct = default) => inner.FindResolvedInterruptionAsync(serverTaskId, stepName, actionName, machineName, ct);

        public Task CancelPendingInterruptionsAsync(int serverTaskId, CancellationToken ct = default) => inner.CancelPendingInterruptionsAsync(serverTaskId, ct);
    }

    private async Task WaitForStateAsync(int taskId, string state)
    {
        var deadline = DateTime.UtcNow + Bound;

        while (DateTime.UtcNow < deadline)
        {
            if ((await LoadTaskAsync(taskId).ConfigureAwait(false)).State == state) return;

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException($"Task {taskId} did not reach {state} within {Bound}");
    }

    private Task<ServerTask> LoadTaskAsync(int taskId) => Run<IServerTaskDataProvider, ServerTask>(p => p.GetServerTaskByIdNoTrackingAsync(taskId, CancellationToken.None));

    private Task ResolveInterruptionAsync(int taskId)
        => Run<IDeploymentInterruptionService>(async service =>
        {
            var pending = await service.GetPendingInterruptionsAsync(taskId, CancellationToken.None).ConfigureAwait(false);

            pending.Count.ShouldBe(1, "exactly one manual-intervention prompt");

            await service.SubmitInterruptionAsync(pending[0].Id, new Dictionary<string, string> { ["Result"] = "Proceed" }, CancellationToken.None).ConfigureAwait(false);
        });

    private Task AssertStateAsync(int taskId, string expected)
        => Run<IServerTaskDataProvider>(async provider => (await provider.GetServerTaskByIdAsync(taskId, CancellationToken.None).ConfigureAwait(false)).State.ShouldBe(expected));

    private Task<string> InFlightTicketAsync(SeededDeployment seeded)
        => Run<IDeploymentCheckpointService, string>(async checkpoints =>
        {
            var checkpoint = await checkpoints.LoadAsync(seeded.TaskId).ConfigureAwait(false);

            return checkpoint == null ? null : InFlightScriptMap.TryGet(checkpoint.InFlightScriptsJson, seeded.DispatchSlot);
        });

    private sealed class LocalClientFactory(IAsyncScriptService client) : IHalibutClientFactory
    {
        public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => client;

        public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => new NoOpCapabilities();

        public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint) => throw new NotSupportedException();

        private sealed class NoOpCapabilities : IAsyncCapabilitiesService
        {
            public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request) => Task.FromResult(new CapabilitiesResponse());
        }
    }
}
