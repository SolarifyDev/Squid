using System.Text.Json;
using Halibut;
using Microsoft.EntityFrameworkCore;
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
/// with only the Halibut RPC transport faked (its <c>GetStatus</c> throws a
/// transient <see cref="HalibutClientException"/>).
///
/// <para>This closes the composition gap the adversarial review on PR #434
/// flagged: the unit tests mock the phase to throw the transient directly, and
/// <c>HalibutResumeReattachTests</c> calls the strategy directly — neither runs
/// the real <c>ExecuteSingleActionAsync</c> per-action catch THROUGH the runner to
/// the pause outcome. The non-required case is the dangerous one: pre-fix a
/// non-required step's transient was swallowed into <c>FailureEncountered</c>,
/// routing to <c>OnFailureAsync</c> (Failed + checkpoint AND in-flight pointer
/// DELETED) and orphaning the still-running agent script. This test proves BOTH
/// required and non-required transients now pause with the checkpoint + in-flight
/// pointer preserved for resume.</para>
/// </summary>
public class IntegrationTransientPause : DeploymentFixtureBase
{
    [Theory]
    [InlineData(true)]    // required step
    [InlineData(false)]   // NON-required step — the swallow path the fix closed
    public async Task TransientInfraFailure_PausesDeployment_PreservesCheckpointAndInFlightPointer(bool isRequired)
    {
        var taskId = await SeedScriptDeploymentAsync(isRequired).ConfigureAwait(false);

        await Run<IDeploymentTaskExecutor>(
            executor => executor.ProcessAsync(taskId, CancellationToken.None),
            extraRegistration: b => b.RegisterInstance(new TransientThrowingHalibutClientFactory())
                .As<IHalibutClientFactory>()
                .SingleInstance()).ConfigureAwait(false);

        await Run<IServerTaskDataProvider, IDeploymentCheckpointService>(async (taskProvider, checkpointService) =>
        {
            var task = await taskProvider.GetServerTaskByIdAsync(taskId, CancellationToken.None).ConfigureAwait(false);

            task.ShouldNotBeNull();
            task.State.ShouldBe(TaskState.Paused,
                customMessage: $"A transient infra failure on a {(isRequired ? "required" : "NON-required")} step must PAUSE the " +
                              "deployment (resumable), not Fail it. If Failed, the transient was swallowed into FailureEncountered " +
                              "instead of propagating to the runner's transient-pause classification.");

            var checkpoint = await checkpointService.LoadAsync(taskId).ConfigureAwait(false);
            checkpoint.ShouldNotBeNull(
                customMessage: "The checkpoint MUST survive a transient pause — it is the resume point. A deleted checkpoint means " +
                              "the failure routed through OnFailureAsync (the orphaning regression this guards).");

            HasInFlightPointer(checkpoint.InFlightScriptsJson).ShouldBeTrue(
                customMessage: "The in-flight script pointer MUST be preserved so a resume re-attaches to the still-running script " +
                              $"instead of re-dispatching a duplicate. InFlightScriptsJson was '{checkpoint.InFlightScriptsJson}'.");
        }).ConfigureAwait(false);
    }

    private static bool HasInFlightPointer(string? json)
        => !string.IsNullOrWhiteSpace(json) && json.Contains("\"t\"", StringComparison.Ordinal);

    private async Task<int> SeedScriptDeploymentAsync(bool isRequired)
    {
        var taskId = 0;

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

            taskId = serverTask.Id;
        }).ConfigureAwait(false);

        return taskId;
    }

    // ── Faked Halibut transport: StartScript succeeds (so the real strategy records
    //    the in-flight pointer), then GetStatus throws a transient drop. ──

    private sealed class TransientThrowingHalibutClientFactory : IHalibutClientFactory
    {
        public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => new TransientThrowingScriptService();

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

    private sealed class TransientThrowingScriptService : IAsyncScriptService
    {
        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command)
            => Task.FromResult(Resp(command.ScriptTicket, ProcessState.Running, 0));

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
            => throw new HalibutClientException("transient: agent dropped mid-script");

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command)
            => Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 0));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command)
            => Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 0));

        private static ScriptStatusResponse Resp(ScriptTicket ticket, ProcessState state, int exitCode)
            => new(ticket, state, exitCode, new List<ProcessOutput>(), 0);
    }
}
