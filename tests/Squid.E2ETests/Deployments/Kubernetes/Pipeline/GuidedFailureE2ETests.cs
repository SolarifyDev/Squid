using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Core.Services.DeploymentExecution.Script;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class GuidedFailureE2ETests
    : IClassFixture<DeploymentPipelineFixture<GuidedFailureE2ETests>>
{
    private readonly DeploymentPipelineFixture<GuidedFailureE2ETests> _fixture;

    public GuidedFailureE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<GuidedFailureE2ETests> fixture)
    {
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    [Theory]
    [InlineData("Retry", TaskState.Success, 2)]
    [InlineData("Ignore", TaskState.Success, 1)]
    [InlineData("Abort", TaskState.Failed, 1)]
    public async Task GuidedFailure_Decision_ControlsPipelineOutcome(string decision, string expectedTaskState, int expectedScriptCount)
    {
        ExecutionCapture.Clear();

        var failOnFirstRun = true;
        ExecutionCapture.ResultFactory = _ =>
        {
            if (failOnFirstRun)
            {
                return new ScriptExecutionResult
                {
                    Success = false,
                    ExitCode = 1,
                    LogLines = new List<string> { "Simulated action failure" }
                };
            }

            return new ScriptExecutionResult { Success = true, ExitCode = 0 };
        };

        var serverTaskId = await SeedGuidedFailurePipelineAsync();

        // Phase 1: pipeline runs → action fails → guided failure → suspends
        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await AssertTaskStateAsync(serverTaskId, TaskState.Paused);

        // Submit decision
        await SubmitGuidedFailureDecisionAsync(serverTaskId, decision);

        // For Retry, make the action succeed on the next run
        if (decision == "Retry")
            failOnFirstRun = false;

        // Phase 2: pipeline resumes → applies decision
        // ProcessAsync → LoadTaskPhase → StartExecutingAsync handles Paused → Executing
        // and sets IsResumed = true, which triggers the guided failure resume path
        try
        {
            await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
            {
                await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (DeploymentAbortedException)
        {
            // Expected when decision is "Abort"
        }

        ExecutionCapture.CapturedRequests.Count.ShouldBe(expectedScriptCount);
        await AssertTaskStateAsync(serverTaskId, expectedTaskState);
    }

    // ========================================================================
    // Interruption Submission
    // ========================================================================

    private async Task SubmitGuidedFailureDecisionAsync(int serverTaskId, string decision)
    {
        await _fixture.Run<IDeploymentInterruptionService>(async service =>
        {
            var pending = await service.GetPendingInterruptionsAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);

            pending.ShouldNotBeEmpty("Expected a pending guided failure interruption");

            var interruption = pending.First();
            interruption.InterruptionType.ShouldBe(InterruptionType.GuidedFailure);

            await service.SubmitInterruptionAsync(interruption.Id, new Dictionary<string, string> { ["Result"] = decision }, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    // ========================================================================
    // Seeder
    // ========================================================================

    private async Task<int> SeedGuidedFailurePipelineAsync()
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

            // Single step with isRequired=true — failure triggers guided failure
            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Step", "Action", "Success", isRequired: true).ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id, ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(step.Id, 1, "Deploy Action", actionType: "Squid.Script").ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'guided-failure-test'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // Infrastructure
            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync("E2E Guided Failure Env").ConfigureAwait(false);

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
                Name = "E2E Guided Failure Target",
                IsDisabled = false,
                Roles = "k8s",
                EnvironmentIds = environment.Id.ToString(),
                Endpoint = endpointJson,
                SpaceId = 1,
                Slug = "e2e-guided-failure-target"
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var account = new DeploymentAccount
            {
                SpaceId = 1,
                Name = "E2E Guided Failure Account",
                Slug = "e2e-guided-failure-account",
                AccountType = AccountType.Token,
                Credentials = DeploymentAccountCredentialsConverter.Serialize(
                    new TokenCredentials { Token = "e2e-test-token" })
            };

            await repository.InsertAsync(account).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            // UseGuidedFailure=true via DeploymentRequestPayload in Deployment.Json
            var deploymentJson = JsonSerializer.Serialize(new DeploymentRequestPayload { UseGuidedFailure = true });

            var deployment = new Deployment
            {
                Name = "E2E Guided Failure Deployment",
                SpaceId = 1,
                ChannelId = channel.Id,
                ProjectId = project.Id,
                ReleaseId = release.Id,
                EnvironmentId = environment.Id,
                DeployedBy = 1,
                CreatedDate = DateTimeOffset.UtcNow,
                Json = deploymentJson
            };

            await repository.InsertAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var serverTask = new ServerTask
            {
                Name = "E2E Guided Failure Task",
                Description = "E2E guided failure test",
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
