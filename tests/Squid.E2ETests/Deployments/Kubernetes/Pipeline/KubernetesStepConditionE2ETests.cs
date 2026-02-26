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
public class KubernetesStepConditionE2ETests
    : IClassFixture<DeploymentPipelineFixture<KubernetesStepConditionE2ETests>>
{
    private readonly KindClusterFixture _cluster;
    private readonly DeploymentPipelineFixture<KubernetesStepConditionE2ETests> _fixture;

    public KubernetesStepConditionE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<KubernetesStepConditionE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    [Fact]
    public async Task Pipeline_AlwaysCondition_RunsRegardlessOfPriorFailure()
    {
        ExecutionCapture.Clear();

        // Step 1 will fail, Step 2 has "Always" condition — should still execute
        var serverTaskId = await SeedTwoStepPipelineAsync(
            step1Condition: "Success",
            step2Condition: "Always",
            step1ShouldFail: true);

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        // Step 2 should have executed despite Step 1 failure
        var executedStepNames = ExecutionCapture.CapturedRequests
            .Select(r => r.ScriptBody)
            .ToList();

        executedStepNames.ShouldContain(
            s => s.Contains("step-2-always"),
            "Step 2 with 'Always' condition should execute even after Step 1 failure");

        // Task completes — step 1 is non-required so pipeline continues
        await AssertTaskStateAsync(TaskState.Success);
    }

    [Fact]
    public async Task Pipeline_SuccessCondition_SkipsAfterFailure()
    {
        ExecutionCapture.Clear();

        // Step 1 will fail, Step 2 has "Success" condition — should be skipped
        var serverTaskId = await SeedTwoStepPipelineAsync(
            step1Condition: "Success",
            step2Condition: "Success",
            step1ShouldFail: true);

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var executedScripts = ExecutionCapture.CapturedRequests
            .Select(r => r.ScriptBody)
            .ToList();

        executedScripts.ShouldNotContain(
            s => s.Contains("step-2-success"),
            "Step 2 with 'Success' condition should be skipped after Step 1 failure");

        // Task completes — step 1 is non-required so pipeline continues
        await AssertTaskStateAsync(TaskState.Success);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Pipeline_FailureCondition_RunsOnlyAfterFailure(
        bool step1ShouldFail, bool expectStep2Executed)
    {
        ExecutionCapture.Clear();

        // Step 2 has "Failure" condition — runs only when Step 1 failed
        var serverTaskId = await SeedTwoStepPipelineAsync(
            step1Condition: "Success",
            step2Condition: "Failure",
            step1ShouldFail: step1ShouldFail);

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var executedScripts = ExecutionCapture.CapturedRequests
            .Select(r => r.ScriptBody)
            .ToList();

        if (expectStep2Executed)
        {
            executedScripts.ShouldContain(
                s => s.Contains("step-2-failure"),
                "Step 2 with 'Failure' condition should run when Step 1 failed");
        }
        else
        {
            executedScripts.ShouldNotContain(
                s => s.Contains("step-2-failure"),
                "Step 2 with 'Failure' condition should be skipped when Step 1 succeeded");
        }
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("False", false)]
    public async Task Pipeline_VariableCondition_EvaluatesExpression(
        string variableValue, bool expectStep2Executed)
    {
        ExecutionCapture.Clear();

        var serverTaskId = await SeedVariableConditionPipelineAsync(
            conditionExpression: "#{RunCleanup}",
            variableName: "RunCleanup",
            variableValue: variableValue);

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var executedScripts = ExecutionCapture.CapturedRequests
            .Select(r => r.ScriptBody)
            .ToList();

        if (expectStep2Executed)
        {
            executedScripts.ShouldContain(
                s => s.Contains("step-2-variable"),
                "Step 2 with Variable condition should execute when expression is truthy");
        }
        else
        {
            executedScripts.ShouldNotContain(
                s => s.Contains("step-2-variable"),
                "Step 2 with Variable condition should be skipped when expression is falsy");
        }
    }

    // ========================================================================
    // Seeder
    // ========================================================================

    private async Task<int> SeedVariableConditionPipelineAsync(
        string conditionExpression, string variableName, string variableValue)
    {
        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            await builder.CreateVariableAsync(variableSet.Id, variableName, variableValue).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            // Step 1 — normal success step
            var step1 = await builder.CreateDeploymentStepAsync(
                process.Id, 1, "Step 1", "Action", "Success").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step1.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action1 = await builder.CreateDeploymentActionAsync(
                step1.Id, 1, "Step 1 Action",
                actionType: "Squid.KubernetesRunScript").ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action1.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action1.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'step-1-script'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // Step 2 — Variable condition with expression
            var step2 = await builder.CreateDeploymentStepAsync(
                process.Id, 2, "Step 2", "Action", "Variable").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step2.Id,
                ("Squid.Action.TargetRoles", "k8s"),
                ("Squid.Step.ConditionExpression", conditionExpression)).ConfigureAwait(false);

            var action2 = await builder.CreateDeploymentActionAsync(
                step2.Id, 1, "Step 2 Action",
                actionType: "Squid.KubernetesRunScript").ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action2.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action2.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'step-2-variable'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // Infrastructure
            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync("E2E Variable Condition Env").ConfigureAwait(false);

            var endpointJson = JsonSerializer.Serialize(new
            {
                CommunicationStyle = "KubernetesApi",
                ClusterUrl = "https://localhost:6443",
                SkipTlsVerification = "True",
                DeploymentAccountId = "1",
                Namespace = "default"
            });

            var machine = new Machine
            {
                Name = "E2E Variable Condition Target",
                IsDisabled = false,
                Roles = "k8s",
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
                Slug = "e2e-variable-condition-target"
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var account = new DeploymentAccount
            {
                SpaceId = 1,
                Name = "E2E Variable Condition Account",
                Slug = "e2e-variable-condition-account",
                AccountType = AccountType.Token,
                Credentials = DeploymentAccountCredentialsConverter.Serialize(
                    new TokenCredentials { Token = "e2e-test-token" })
            };

            await repository.InsertAsync(account).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "E2E Variable Condition Deployment",
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

            var serverTask = new ServerTask
            {
                Name = "E2E Variable Condition Task",
                Description = "E2E variable condition test",
                QueueTime = DateTimeOffset.UtcNow,
                State = TaskState.Pending,
                ServerTaskType = "Deploy",
                ProjectId = project.Id,
                EnvironmentId = environment.Id,
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

            await repository.InsertAsync(serverTask).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            deployment.TaskId = serverTask.Id;
            await repository.UpdateAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            serverTaskId = serverTask.Id;
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    private async Task<int> SeedTwoStepPipelineAsync(
        string step1Condition, string step2Condition, bool step1ShouldFail)
    {
        // Configure failure for step 1 if needed
        if (step1ShouldFail)
        {
            ExecutionCapture.ResultFactory = request =>
            {
                if (request.ScriptBody.Contains("step-1-script"))
                {
                    return new ScriptExecutionResult
                    {
                        Success = false,
                        ExitCode = 1,
                        LogLines = new List<string> { "Step 1 simulated failure" }
                    };
                }

                return new ScriptExecutionResult { Success = true, ExitCode = 0 };
            };
        }

        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            // Step 1 — isRequired: false so failure is tracked without aborting the pipeline
            var step1 = await builder.CreateDeploymentStepAsync(
                process.Id, 1, "Step 1", "Action", step1Condition, isRequired: false).ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step1.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action1 = await builder.CreateDeploymentActionAsync(
                step1.Id, 1, "Step 1 Action",
                actionType: "Squid.KubernetesRunScript").ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action1.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action1.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'step-1-script'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // Step 2
            var step2 = await builder.CreateDeploymentStepAsync(
                process.Id, 2, "Step 2", "Action", step2Condition).ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step2.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var step2ScriptMarker = step2Condition.ToLowerInvariant();
            var action2 = await builder.CreateDeploymentActionAsync(
                step2.Id, 1, "Step 2 Action",
                actionType: "Squid.KubernetesRunScript").ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action2.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action2.Id,
                ("Squid.Action.Script.ScriptBody", $"echo 'step-2-{step2ScriptMarker}'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // Infrastructure
            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync("E2E Step Condition Env").ConfigureAwait(false);

            var endpointJson = JsonSerializer.Serialize(new
            {
                CommunicationStyle = "KubernetesApi",
                ClusterUrl = "https://localhost:6443",
                SkipTlsVerification = "True",
                DeploymentAccountId = "1",
                Namespace = "default"
            });

            var machine = new Machine
            {
                Name = "E2E Condition Target",
                IsDisabled = false,
                Roles = "k8s",
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
                Slug = "e2e-condition-target"
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var account = new DeploymentAccount
            {
                SpaceId = 1,
                Name = "E2E Condition Account",
                Slug = "e2e-condition-account",
                AccountType = AccountType.Token,
                Credentials = DeploymentAccountCredentialsConverter.Serialize(
                    new TokenCredentials { Token = "e2e-test-token" })
            };

            await repository.InsertAsync(account).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "E2E Step Condition Deployment",
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

            var serverTask = new ServerTask
            {
                Name = "E2E Step Condition Task",
                Description = "E2E step condition test",
                QueueTime = DateTimeOffset.UtcNow,
                State = TaskState.Pending,
                ServerTaskType = "Deploy",
                ProjectId = project.Id,
                EnvironmentId = environment.Id,
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
