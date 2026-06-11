using System.Text.Json;
using Halibut;
using Halibut.Exceptions;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Enums;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;
using Machine = Squid.Core.Persistence.Entities.Deployments.Machine;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// End-to-end coverage for P1b "transient infra failure → Paused (resumable)"
/// through the REAL pipeline: a full <see cref="IDeploymentTaskExecutor.ProcessAsync"/>
/// run drives the real <c>ExecuteStepsPhase</c> + the real
/// <c>DeploymentPipelineRunner</c> + the real <see cref="IInFlightScriptStore"/>,
/// with only the Halibut RPC transport faked (its <c>GetStatus</c> throws).
///
/// <para>This closes the composition gap the adversarial review on PR #434
/// flagged: the unit tests mock the phase to throw the transient directly, and
/// <c>HalibutResumeReattachTests</c> calls the strategy directly — neither runs
/// the real <c>ExecuteSingleActionAsync</c> per-action catch THROUGH the runner to
/// the pause outcome. The non-required case is the dangerous one: pre-fix a
/// non-required step's transient was swallowed into <c>FailureEncountered</c>,
/// routing to <c>OnFailureAsync</c> (Failed + checkpoint AND in-flight pointer
/// DELETED) and orphaning the still-running agent script. This test proves BOTH
/// required and non-required transients pause with the checkpoint + in-flight
/// pointer preserved for resume.</para>
///
/// <para>The <see cref="PermanentInfraFailure_FailsDeployment_DeletesCheckpoint"/>
/// contrast case feeds the SAME seeded path a PERMANENT Halibut rejection and
/// asserts the opposite outcome (Failed + checkpoint deleted), so the Paused
/// assertions read as "Paused IFF transient" rather than "always Paused" — it
/// pins the production transient/permanent boundary
/// (<see cref="Squid.Core.Halibut.Resilience.TransientFailureClassifier"/>)
/// end-to-end, not just in the predicate's own unit test.</para>
/// </summary>
public class IntegrationTransientPause : DeploymentFixtureBase
{
    [Theory]
    [InlineData(true)]    // required step
    [InlineData(false)]   // NON-required step — the swallow path the fix closed
    public async Task TransientInfraFailure_PausesDeployment_PreservesCheckpointAndInFlightPointer(bool isRequired)
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired).ConfigureAwait(false);

        var factory = new FaultInjectingHalibutClientFactory(() => new HalibutClientException("transient: agent dropped mid-script"));

        await Run<IDeploymentTaskExecutor>(
            executor => executor.ProcessAsync(seeded.TaskId, CancellationToken.None),
            extraRegistration: b => b.RegisterInstance(factory).As<IHalibutClientFactory>().SingleInstance()).ConfigureAwait(false);

        await Run<IServerTaskDataProvider, IDeploymentCheckpointService>(async (taskProvider, checkpointService) =>
        {
            var task = await taskProvider.GetServerTaskByIdAsync(seeded.TaskId, CancellationToken.None).ConfigureAwait(false);

            task.ShouldNotBeNull();
            task.State.ShouldBe(TaskState.Paused,
                customMessage: $"A transient infra failure on a {(isRequired ? "required" : "NON-required")} step must PAUSE the " +
                              "deployment (resumable), not Fail it. If Failed, the transient was swallowed into FailureEncountered " +
                              "instead of propagating to the runner's transient-pause classification.");

            var checkpoint = await checkpointService.LoadAsync(seeded.TaskId).ConfigureAwait(false);
            checkpoint.ShouldNotBeNull(
                customMessage: "The checkpoint MUST survive a transient pause — it is the resume point. A deleted checkpoint means " +
                              "the failure routed through OnFailureAsync (the orphaning regression this guards).");

            // Structural assertion via the SAME production reader the resume path
            // uses — not a substring match — so a JsonPropertyName rename on the
            // pointer entry surfaces here instead of silently passing.
            factory.DispatchedTicket.ShouldNotBeNullOrEmpty(
                customMessage: "The faked StartScript must have observed (and the strategy recorded) a ScriptTicket — without it the " +
                              "ticket-equality assertion below would be vacuously comparing null to null.");

            var preserved = InFlightScriptMap.TryGet(checkpoint.InFlightScriptsJson, seeded.DispatchSlot);
            preserved.ShouldBe(factory.DispatchedTicket,
                customMessage: "The in-flight pointer for this dispatch slot MUST hold the EXACT ticket the server dispatched, so a " +
                              "resume re-attaches to the still-running script instead of launching a duplicate. " +
                              $"InFlightScriptsJson was '{checkpoint.InFlightScriptsJson}'.");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task PermanentInfraFailure_FailsDeployment_DeletesCheckpoint()
    {
        // Contrast to the transient rows: a PERMANENT Halibut protocol rejection
        // (the request reached the agent and was permanently rejected — version
        // mismatch / missing service) is NOT a resumable blip. It must fail
        // terminally (runner → OnFailureAsync → Failed + checkpoint deleted),
        // proving the Paused outcome above is "Paused IFF transient". A regression
        // that over-broadens the transient predicate to match this would flip THIS
        // row red while leaving the Paused rows green.
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);

        var factory = new FaultInjectingHalibutClientFactory(() => new ServiceNotFoundHalibutClientException("permanent: agent exposes no such service"));

        await Should.ThrowAsync<Exception>(() => Run<IDeploymentTaskExecutor>(
            executor => executor.ProcessAsync(seeded.TaskId, CancellationToken.None),
            extraRegistration: b => b.RegisterInstance(factory).As<IHalibutClientFactory>().SingleInstance())).ConfigureAwait(false);

        await Run<IServerTaskDataProvider, IDeploymentCheckpointService>(async (taskProvider, checkpointService) =>
        {
            var task = await taskProvider.GetServerTaskByIdAsync(seeded.TaskId, CancellationToken.None).ConfigureAwait(false);

            task.ShouldNotBeNull();
            task.State.ShouldBe(TaskState.Failed,
                customMessage: "A PERMANENT (non-transient) Halibut rejection must FAIL the deployment terminally, not pause it. If " +
                              "Paused, the transient predicate wrongly matched a permanent protocol failure — pausing would pause-loop " +
                              "on a genuinely broken agent.");

            var checkpoint = await checkpointService.LoadAsync(seeded.TaskId).ConfigureAwait(false);
            checkpoint.ShouldBeNull(
                customMessage: "OnFailureAsync MUST delete the checkpoint on a terminal failure. A surviving checkpoint means the " +
                              "permanent failure was mis-routed to the transient-pause path.");
        }).ConfigureAwait(false);
    }

    private async Task<SeededDeployment> SeedScriptDeploymentAsync(bool isRequired)
    {
        SeededDeployment? result = null;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Run script", isRequired: isRequired).ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id, ("Squid.Action.TargetRoles", "web")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(step.Id, 1, "Run script", actionType: SpecialVariables.ActionTypes.Script).ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action.Id, "web").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id,
                ("Squid.Action.Script.ScriptBody", "echo transient-pause-test"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);

            var environment = new Environment
            {
                SpaceId = 1,
                Slug = $"transient-env-{Guid.NewGuid():N}"[..20],
                Name = "Transient Pause Env",
                Description = "Environment for transient-pause E2E",
                SortOrder = 0,
                UseGuidedFailure = false,
                AllowDynamicInfrastructure = false,
                LastModifiedDate = DateTimeOffset.UtcNow,
                LastModifiedBy = 0
            };

            await repository.InsertAsync(environment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            // KubernetesAgent so the REAL HalibutMachineExecutionStrategy is used
            // (it records the in-flight pointer); the faked client makes GetStatus throw.
            var endpointJson = JsonSerializer.Serialize(new
            {
                CommunicationStyle = "KubernetesAgent",
                SubscriptionId = $"sub-{Guid.NewGuid():N}",
                Thumbprint = "AA11BB22CC33DD44",
                Namespace = "default"
            });

            var machine = new Machine
            {
                Name = "Transient Pause Agent",
                IsDisabled = false,
                Roles = "[\"web\"]",
                EnvironmentIds = $"[{environment.Id}]",
                Endpoint = endpointJson,
                SpaceId = 1,
                Slug = $"transient-agent-{Guid.NewGuid():N}"[..20]
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "Transient Pause Deployment",
                SpaceId = 1,
                ChannelId = channel.Id,
                ProjectId = project.Id,
                ReleaseId = release.Id,
                EnvironmentId = environment.Id,
                DeployedBy = 1,
                CreatedDate = DateTimeOffset.UtcNow,
                Json = string.Empty
            };

            await repository.InsertAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var serverTask = new ServerTask
            {
                Name = "Transient Pause Task",
                Description = "Transient-pause E2E deployment task",
                QueueTime = DateTimeOffset.UtcNow,
                State = TaskState.Pending,
                ServerTaskType = "Deploy",
                ProjectId = project.Id,
                EnvironmentId = environment.Id,
                SpaceId = 1,
                LastModifiedDate = DateTimeOffset.UtcNow,
                BusinessProcessState = "Queued",
                StateOrder = 1,
                Weight = 1,
                BatchId = 0,
                JSON = string.Empty,
                HasWarningsOrErrors = false,
                ServerNodeId = Guid.NewGuid(),
                DurationSeconds = 0,
                DataVersion = Array.Empty<byte>()
            };

            await repository.InsertAsync(serverTask).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            deployment.TaskId = serverTask.Id;
            await repository.UpdateAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            // The snapshot preserves the entity step/action IDs (DeploymentSnapshotService
            // copies step.Id / action.Id verbatim), and the strategy records the pointer
            // keyed by (Machine.Id, request.StepId, request.ActionId) — so the dispatch
            // slot the production code writes is exactly these seeded IDs.
            result = new SeededDeployment(serverTask.Id, new DispatchSlot(machine.Id, step.Id, action.Id));
        }).ConfigureAwait(false);

        return result!;
    }

    private sealed record SeededDeployment(int TaskId, DispatchSlot DispatchSlot);

    // ── Faked Halibut transport: StartScript succeeds (so the real strategy records
    //    the in-flight pointer + we capture the dispatched ticket), then GetStatus
    //    throws the injected fault (transient OR permanent). ──

    private sealed class FaultInjectingHalibutClientFactory : IHalibutClientFactory
    {
        private readonly Func<Exception> _getStatusFault;

        public FaultInjectingHalibutClientFactory(Func<Exception> getStatusFault) => _getStatusFault = getStatusFault;

        // The server-generated ScriptTicket the strategy ALSO records into the
        // in-flight pointer; the transient test asserts the preserved pointer
        // holds exactly this value.
        public string? DispatchedTicket { get; private set; }

        public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => new FaultInjectingScriptService(_getStatusFault, t => DispatchedTicket = t);

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
                => throw new NotSupportedException("transient-pause test does not exercise file transfer");

            public Task<DataStream> DownloadFileAsync(string remotePath)
                => throw new NotSupportedException("transient-pause test does not exercise file transfer");
        }
    }

    private sealed class FaultInjectingScriptService : IAsyncScriptService
    {
        private readonly Func<Exception> _getStatusFault;
        private readonly Action<string> _onDispatched;

        public FaultInjectingScriptService(Func<Exception> getStatusFault, Action<string> onDispatched)
        {
            _getStatusFault = getStatusFault;
            _onDispatched = onDispatched;
        }

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command)
        {
            _onDispatched(command.ScriptTicket.TaskId);
            return Task.FromResult(Resp(command.ScriptTicket, ProcessState.Running, 0));
        }

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request) => throw _getStatusFault();

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command)
            => Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 0));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command)
            => Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 0));

        private static ScriptStatusResponse Resp(ScriptTicket ticket, ProcessState state, int exitCode)
            => new(ticket, state, exitCode, new List<ProcessOutput>(), 0);
    }
}
