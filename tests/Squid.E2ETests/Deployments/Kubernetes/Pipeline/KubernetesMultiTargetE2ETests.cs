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

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class KubernetesMultiTargetE2ETests
    : IClassFixture<DeploymentPipelineFixture<KubernetesMultiTargetE2ETests>>
{
    private readonly KindClusterFixture _cluster;
    private readonly DeploymentPipelineFixture<KubernetesMultiTargetE2ETests> _fixture;

    public KubernetesMultiTargetE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<KubernetesMultiTargetE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    [Fact]
    public async Task Pipeline_TwoTargets_BothReceiveDeployment()
    {
        ExecutionCapture.Clear();

        var serverTaskId = await SeedWithTwoMatchingTargetsAsync("k8s");

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await AssertTaskStateAsync(TaskState.Success);

        // Both targets should have received execution requests
        ExecutionCapture.CapturedRequests.Count.ShouldBeGreaterThanOrEqualTo(2,
            "Both matching targets should receive deployment scripts");

        var machineNames = ExecutionCapture.CapturedRequests
            .Select(r => r.Machine.Name)
            .Distinct()
            .ToList();

        machineNames.ShouldContain("E2E Target Alpha");
        machineNames.ShouldContain("E2E Target Beta");
    }

    [Fact]
    public async Task Pipeline_TargetRoleMismatch_SkipsNonMatchingTarget()
    {
        ExecutionCapture.Clear();

        // Step requires role "web", but second machine has role "db"
        var serverTaskId = await SeedWithMismatchedRolesAsync();

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await AssertTaskStateAsync(TaskState.Success);

        // Only the matching target should have received execution
        var executedMachines = ExecutionCapture.CapturedRequests
            .Select(r => r.Machine.Name)
            .Distinct()
            .ToList();

        executedMachines.ShouldContain("E2E Web Target");
        executedMachines.ShouldNotContain("E2E DB Target",
            "Target with non-matching role should be skipped");
    }

    [Fact]
    public async Task Pipeline_OneTargetFails_OverallTaskFails()
    {
        ExecutionCapture.Clear();

        var serverTaskId = await SeedWithTwoMatchingTargetsAsync("k8s");

        // Configure second target to fail
        ExecutionCapture.ResultFactory = request =>
        {
            if (request.Machine.Name == "E2E Target Beta")
            {
                return new ScriptExecutionResult
                {
                    Success = false,
                    ExitCode = 1,
                    LogLines = new List<string> { "Simulated failure on Beta target" }
                };
            }

            return new ScriptExecutionResult { Success = true, ExitCode = 0 };
        };

        // ProcessAsync records failure then re-throws — catch the expected exception
        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await Should.ThrowAsync<Exception>(
                () => executor.ProcessAsync(serverTaskId, CancellationToken.None)).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await AssertTaskStateAsync(TaskState.Failed);

        // Alpha should have been attempted before Beta failed
        var executedMachines = ExecutionCapture.CapturedRequests
            .Select(r => r.Machine.Name)
            .Distinct()
            .ToList();

        executedMachines.ShouldContain("E2E Target Alpha", "First target should have been executed");
    }

    // ========================================================================
    // Seeders
    // ========================================================================

    private async Task<int> SeedWithTwoMatchingTargetsAsync(string role)
    {
        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateVariablesAsync(variableSet.Id,
                ("Namespace", "default", VariableType.String, false)).ConfigureAwait(false);

            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Script").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                ("Squid.Action.TargetRoles", role)).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, "Run Script",
                actionType: "Squid.KubernetesRunScript").ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action.Id, role).ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'deployed'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync("E2E Multi Target Env").ConfigureAwait(false);

            // Machine Alpha
            await InsertMachineAsync(repository, unitOfWork,
                "E2E Target Alpha", role, environment, "KubernetesApi", "e2e-alpha").ConfigureAwait(false);

            // Machine Beta
            await InsertMachineAsync(repository, unitOfWork,
                "E2E Target Beta", role, environment, "KubernetesApi", "e2e-beta").ConfigureAwait(false);

            // Account
            var account = new DeploymentAccount
            {
                SpaceId = 1,
                Name = "E2E Multi Account",
                Slug = "e2e-multi-account",
                AccountType = AccountType.Token,
                Credentials = DeploymentAccountCredentialsConverter.Serialize(
                    new TokenCredentials { Token = "e2e-test-token" })
            };

            await repository.InsertAsync(account).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "E2E Multi Target Deployment",
                SpaceId = 1,
                ChannelId = channel.Id,
                ProjectId = project.Id,
                ReleaseId = release.Id,
                EnvironmentId = environment.Id,
                DeployedBy = 1,
                Created = DateTimeOffset.UtcNow,
                Json = string.Empty
            };

            await repository.InsertAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var serverTask = CreateServerTask(project.Id, environment.Id);
            await repository.InsertAsync(serverTask).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            deployment.TaskId = serverTask.Id;
            await repository.UpdateAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            serverTaskId = serverTask.Id;
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    private async Task<int> SeedWithMismatchedRolesAsync()
    {
        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateVariablesAsync(variableSet.Id,
                ("Namespace", "default", VariableType.String, false)).ConfigureAwait(false);

            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            // Step requires role "web"
            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Web").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                ("Squid.Action.TargetRoles", "web")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, "Run Web Script",
                actionType: "Squid.KubernetesRunScript").ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action.Id, "web").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'web deployed'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync("E2E Role Mismatch Env").ConfigureAwait(false);

            // Machine with matching role "web"
            await InsertMachineAsync(repository, unitOfWork,
                "E2E Web Target", "web", environment, "KubernetesApi", "e2e-web").ConfigureAwait(false);

            // Machine with non-matching role "db"
            await InsertMachineAsync(repository, unitOfWork,
                "E2E DB Target", "db", environment, "KubernetesApi", "e2e-db").ConfigureAwait(false);

            var account = new DeploymentAccount
            {
                SpaceId = 1,
                Name = "E2E Mismatch Account",
                Slug = "e2e-mismatch-account",
                AccountType = AccountType.Token,
                Credentials = DeploymentAccountCredentialsConverter.Serialize(
                    new TokenCredentials { Token = "e2e-test-token" })
            };

            await repository.InsertAsync(account).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "E2E Role Mismatch Deployment",
                SpaceId = 1,
                ChannelId = channel.Id,
                ProjectId = project.Id,
                ReleaseId = release.Id,
                EnvironmentId = environment.Id,
                DeployedBy = 1,
                Created = DateTimeOffset.UtcNow,
                Json = string.Empty
            };

            await repository.InsertAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var serverTask = CreateServerTask(project.Id, environment.Id);
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

    private static async Task InsertMachineAsync(
        IRepository repository, IUnitOfWork unitOfWork,
        string name, string roles, Environment environment,
        string communicationStyle, string slugSuffix)
    {
        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = communicationStyle,
            ClusterUrl = "https://localhost:6443",
            SkipTlsVerification = "True",
            DeploymentAccountId = "1",
            Namespace = "default"
        });

        var machine = new Machine
        {
            Name = name,
            IsDisabled = false,
            Roles = roles,
            EnvironmentIds = environment.Id.ToString(),
            Json = "{\"Endpoint\":{\"Uri\":\"https://localhost:10933\",\"Thumbprint\":\"E2E-THUMBPRINT\"}}",
            Thumbprint = "E2E-THUMBPRINT",
            Uri = "https://localhost:10933",
            HasLatestCalamari = false,
            Endpoint = endpointJson,
            DataVersion = Array.Empty<byte>(),
            SpaceId = 1,
            OperatingSystem = OperatingSystemType.Windows,
            ShellName = "PowerShell",
            ShellVersion = "7.0",
            LicenseHash = string.Empty,
            Slug = $"e2e-{slugSuffix}"
        };

        await repository.InsertAsync(machine).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
    }

    private static ServerTask CreateServerTask(int projectId, int environmentId)
    {
        return new ServerTask
        {
            Name = "E2E Multi Target Task",
            Description = "E2E multi-target test",
            QueueTime = DateTimeOffset.UtcNow,
            State = TaskState.Pending,
            ServerTaskType = "Deploy",
            ProjectId = projectId,
            EnvironmentId = environmentId,
            SpaceId = 1,
            LastModified = DateTimeOffset.UtcNow,
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
