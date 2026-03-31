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
using Squid.Message.Models.Deployments.Machine;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;
using Squid.Core.Services.DeploymentExecution.Filtering;

namespace Squid.E2ETests.Deployments.Pipeline;

[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class RunOnServerE2ETests
    : IClassFixture<DeploymentPipelineFixture<RunOnServerE2ETests>>
{
    private readonly KindClusterFixture _cluster;
    private readonly DeploymentPipelineFixture<RunOnServerE2ETests> _fixture;

    public RunOnServerE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<RunOnServerE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    [Fact]
    public async Task PureRunOnServerDeployment_NoMachines_Succeeds()
    {
        ExecutionCapture.Clear();

        var serverTaskId = await SeedRunOnServerOnlyPipelineAsync();

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        ExecutionCapture.CapturedRequests.Count.ShouldBe(1);

        var captured = ExecutionCapture.CapturedRequests[0];
        captured.ScriptBody.ShouldContain("run-on-server-script");
        captured.Machine.Name.ShouldBe("Squid Server");
        captured.Machine.Id.ShouldBe(0);

        await AssertTaskStateAsync(TaskState.Success);
    }

    [Fact]
    public async Task MixedDeployment_RunOnServerAndTargetSteps_BothExecute()
    {
        ExecutionCapture.Clear();

        var serverTaskId = await SeedMixedPipelineAsync();

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        ExecutionCapture.CapturedRequests.Count.ShouldBe(2);

        var serverRequest = ExecutionCapture.CapturedRequests.First(r => r.ScriptBody.Contains("server-step-script"));
        serverRequest.Machine.Name.ShouldBe("Squid Server");

        var targetRequest = ExecutionCapture.CapturedRequests.First(r => r.ScriptBody.Contains("target-step-script"));
        targetRequest.Machine.Name.ShouldBe("E2E RunOnServer Target");

        await AssertTaskStateAsync(TaskState.Success);
    }

    // ========================================================================
    // Seeders
    // ========================================================================

    private async Task<int> SeedRunOnServerOnlyPipelineAsync()
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

            // Single RunOnServer step — no target roles needed
            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Run On Server Step").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                ("Squid.Action.RunOnServer", "true")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, "Server Action",
                actionType: "Squid.Script").ConfigureAwait(false);

            await builder.CreateActionPropertiesAsync(action.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'run-on-server-script'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync("E2E RunOnServer Env").ConfigureAwait(false);

            // NO machines — pure server-only deployment
            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "E2E RunOnServer Deployment",
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

            var serverTask = CreateServerTask(project, environment);

            await repository.InsertAsync(serverTask).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            deployment.TaskId = serverTask.Id;
            await repository.UpdateAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            serverTaskId = serverTask.Id;
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    private async Task<int> SeedMixedPipelineAsync()
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

            // Step 1 — RunOnServer
            var step1 = await builder.CreateDeploymentStepAsync(process.Id, 1, "Server Step").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step1.Id,
                ("Squid.Action.RunOnServer", "true")).ConfigureAwait(false);

            var action1 = await builder.CreateDeploymentActionAsync(
                step1.Id, 1, "Server Action",
                actionType: "Squid.Script").ConfigureAwait(false);

            await builder.CreateActionPropertiesAsync(action1.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'server-step-script'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // Step 2 — normal target step
            var step2 = await builder.CreateDeploymentStepAsync(process.Id, 2, "Target Step").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step2.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action2 = await builder.CreateDeploymentActionAsync(
                step2.Id, 1, "Target Action",
                actionType: "Squid.Script").ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action2.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action2.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'target-step-script'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // Infrastructure
            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync("E2E Mixed RunOnServer Env").ConfigureAwait(false);

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
                Name = "E2E RunOnServer Target",
                IsDisabled = false,
                Roles = "k8s",
                EnvironmentIds = environment.Id.ToString(),
                Endpoint = endpointJson,
                SpaceId = 1,
                Slug = "e2e-runonserver-target"
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var account = new DeploymentAccount
            {
                SpaceId = 1,
                Name = "E2E RunOnServer Account",
                Slug = "e2e-runonserver-account",
                AccountType = AccountType.Token,
                Credentials = DeploymentAccountCredentialsConverter.Serialize(
                    new TokenCredentials { Token = "e2e-test-token" })
            };

            await repository.InsertAsync(account).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "E2E Mixed RunOnServer Deployment",
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

            var serverTask = CreateServerTask(project, environment);

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
    // Helpers
    // ========================================================================

    private static ServerTask CreateServerTask(Project project, Environment environment)
    {
        return new ServerTask
        {
            Name = "E2E RunOnServer Task",
            Description = "E2E RunOnServer test",
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
    }

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
