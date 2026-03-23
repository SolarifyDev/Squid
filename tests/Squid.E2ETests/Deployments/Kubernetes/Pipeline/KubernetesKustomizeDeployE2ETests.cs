using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class KubernetesKustomizeDeployE2ETests
    : IClassFixture<DeploymentPipelineFixture<KubernetesKustomizeDeployE2ETests>>
{
    private readonly KindClusterFixture _cluster;
    private readonly DeploymentPipelineFixture<KubernetesKustomizeDeployE2ETests> _fixture;

    public KubernetesKustomizeDeployE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<KubernetesKustomizeDeployE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task Pipeline_Kustomize_CapturesKustomizeBuildScript(string communicationStyle)
    {
        ExecutionCapture.Clear();

        var serverTaskId = await SeedKustomizePipelineAsync(communicationStyle);

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        ExecutionCapture.CapturedRequests.ShouldNotBeEmpty("Expected at least one captured script execution request");

        var capturedScript = ExecutionCapture.CapturedRequests[0].ScriptBody;
        capturedScript.ShouldContain("kustomize", Case.Insensitive, "Script should contain kustomize build command");
        capturedScript.ShouldContain("kubectl", Case.Insensitive, "Script should contain kubectl apply");

        await AssertTaskStateAsync(TaskState.Success);
    }

    // ========================================================================
    // Seeder
    // ========================================================================

    private async Task<int> SeedKustomizePipelineAsync(string communicationStyle)
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

            var step = await builder.CreateDeploymentStepAsync(
                process.Id, 1, "Deploy Kustomize", "Action", "Success").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, "Kustomize Deploy",
                actionType: "Squid.KubernetesKustomize").ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id,
                ("Squid.Action.KubernetesKustomize.OverlayPath", "."),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync("E2E Kustomize Env").ConfigureAwait(false);

            var endpointJson = JsonSerializer.Serialize(new
            {
                CommunicationStyle = communicationStyle,
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
                Name = "E2E Kustomize Target",
                IsDisabled = false,
                Roles = "k8s",
                EnvironmentIds = environment.Id.ToString(),
                Endpoint = endpointJson,
                DataVersion = Array.Empty<byte>(),
                SpaceId = 1,
                Slug = $"e2e-kustomize-{communicationStyle.ToLowerInvariant()}"
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var account = new DeploymentAccount
            {
                SpaceId = 1,
                Name = "E2E Kustomize Account",
                Slug = "e2e-kustomize-account",
                AccountType = AccountType.Token,
                Credentials = DeploymentAccountCredentialsConverter.Serialize(
                    new TokenCredentials { Token = "e2e-test-token" })
            };

            await repository.InsertAsync(account).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "E2E Kustomize Deployment",
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
                Name = "E2E Kustomize Task",
                Description = "E2E kustomize pipeline test",
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

    private async Task AssertTaskStateAsync(string expectedState)
    {
        await _fixture.Run<IServerTaskDataProvider>(async taskDataProvider =>
        {
            var tasks = await taskDataProvider.GetAllServerTasksAsync(CancellationToken.None).ConfigureAwait(false);

            tasks.ShouldNotBeNull();
            tasks.Count.ShouldBeGreaterThanOrEqualTo(1);

            var task = tasks.OrderByDescending(t => t.Id).First();
            task.State.ShouldBe(expectedState, $"Expected task state '{expectedState}' but was '{task.State}'");
        }).ConfigureAwait(false);
    }
}
