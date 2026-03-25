using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Core.Services.DeploymentExecution.Script;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class CheckpointCleanupE2ETests
    : IClassFixture<DeploymentPipelineFixture<CheckpointCleanupE2ETests>>
{
    private readonly DeploymentPipelineFixture<CheckpointCleanupE2ETests> _fixture;

    public CheckpointCleanupE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<CheckpointCleanupE2ETests> fixture)
    {
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    [Fact]
    public async Task Pipeline_FailedStep_CleansUpCheckpointOnCompletion()
    {
        ExecutionCapture.Clear();

        // Step 2 fails, but isRequired=false so pipeline completes without throwing.
        // FailureEncountered=true triggers OnFailureAsync → CleanupCheckpointAsync → checkpoint deleted.
        ExecutionCapture.ResultFactory = request =>
        {
            if (request.ScriptBody.Contains("step-2-script"))
            {
                return new ScriptExecutionResult
                {
                    Success = false,
                    ExitCode = 1,
                    LogLines = new List<string> { "Step 2 simulated failure" }
                };
            }

            return new ScriptExecutionResult { Success = true, ExitCode = 0 };
        };

        var serverTaskId = await SeedTwoStepPipelineAsync();

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await AssertTaskStateAsync(serverTaskId, TaskState.Failed);

        // Checkpoint should have been cleaned up by OnFailureAsync
        await _fixture.Run<IDeploymentCheckpointService>(async checkpointService =>
        {
            var checkpoint = await checkpointService.LoadAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            checkpoint.ShouldBeNull("Checkpoint should be deleted after pipeline failure");
        }).ConfigureAwait(false);
    }

    // ========================================================================
    // Seeder
    // ========================================================================

    private async Task<int> SeedTwoStepPipelineAsync()
    {
        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            // Step 1 — succeeds (isRequired: true)
            var step1 = await builder.CreateDeploymentStepAsync(process.Id, 1, "Step 1", "Action", "Success", isRequired: true).ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step1.Id, ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action1 = await builder.CreateDeploymentActionAsync(step1.Id, 1, "Step 1 Action", actionType: "Squid.KubernetesRunScript").ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action1.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action1.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'step-1-script'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // Step 2 — fails (isRequired: false so pipeline continues)
            var step2 = await builder.CreateDeploymentStepAsync(process.Id, 2, "Step 2", "Action", "Success", isRequired: false).ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step2.Id, ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action2 = await builder.CreateDeploymentActionAsync(step2.Id, 1, "Step 2 Action", actionType: "Squid.KubernetesRunScript").ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action2.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action2.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'step-2-script'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // Infrastructure
            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync("E2E Checkpoint Cleanup Env").ConfigureAwait(false);

            var endpointJson = JsonSerializer.Serialize(new
            {
                CommunicationStyle = "KubernetesApi",
                ClusterUrl = "https://localhost:6443",
                SkipTlsVerification = "True",
                Namespace = "default",
                ResourceReferences = new[]
                {
                    new { Type = (int)EndpointResourceType.AuthenticationAccount, ResourceId = 1 }
                }
            });

            var machine = new Machine
            {
                Name = "E2E Checkpoint Cleanup Target",
                IsDisabled = false,
                Roles = "k8s",
                EnvironmentIds = environment.Id.ToString(),
                Endpoint = endpointJson,
                SpaceId = 1,
                Slug = "e2e-checkpoint-cleanup-target"
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var account = new DeploymentAccount
            {
                SpaceId = 1,
                Name = "E2E Checkpoint Cleanup Account",
                Slug = "e2e-checkpoint-cleanup-account",
                AccountType = AccountType.Token,
                Credentials = DeploymentAccountCredentialsConverter.Serialize(
                    new TokenCredentials { Token = "e2e-test-token" })
            };

            await repository.InsertAsync(account).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "E2E Checkpoint Cleanup Deployment",
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
                Name = "E2E Checkpoint Cleanup Task",
                Description = "E2E checkpoint cleanup test",
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

            serverTaskId = serverTask.Id;
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    // ========================================================================
    // Assertions
    // ========================================================================

    private async Task AssertTaskStateAsync(int serverTaskId, string expectedState)
    {
        await _fixture.Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);

            task.ShouldNotBeNull();
            task.State.ShouldBe(expectedState, $"Expected task state '{expectedState}' but was '{task.State}'");
        }).ConfigureAwait(false);
    }
}
