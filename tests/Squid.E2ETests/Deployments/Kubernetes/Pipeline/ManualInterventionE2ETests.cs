using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Account;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class ManualInterventionE2ETests
    : IClassFixture<DeploymentPipelineFixture<ManualInterventionE2ETests>>
{
    private readonly DeploymentPipelineFixture<ManualInterventionE2ETests> _fixture;

    public ManualInterventionE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<ManualInterventionE2ETests> fixture)
    {
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    [Theory]
    [InlineData("Proceed", 2, TaskState.Success)]
    [InlineData("Abort", 1, TaskState.Failed)]
    public async Task ManualIntervention_Decision_ControlsPipelineOutcome(string decision, int expectedScriptCount, string expectedTaskState)
    {
        ExecutionCapture.Clear();

        var serverTaskId = await SeedManualInterventionPipelineAsync(
            preStepScript: "echo 'before-manual'",
            postStepScript: "echo 'after-manual'",
            instructions: "Please approve deployment");

        await RunPipelineWithInterventionAsync(serverTaskId, decision);

        ExecutionCapture.CapturedRequests.Count.ShouldBe(expectedScriptCount, "Manual intervention step itself should not produce script executions");

        var executedScripts = ExecutionCapture.CapturedRequests.Select(r => r.ScriptBody).ToList();
        executedScripts.ShouldContain(s => s.Contains("before-manual"), "Pre-manual step should have executed");

        if (decision == "Proceed")
            executedScripts.ShouldContain(s => s.Contains("after-manual"), "Post-manual step should execute after Proceed");
        else
            executedScripts.ShouldNotContain(s => s.Contains("after-manual"), "Post-manual step should NOT execute after Abort");

        await AssertTaskStateAsync(serverTaskId, expectedTaskState);
    }

    [Fact]
    public async Task ManualIntervention_InterruptionPersistedWithForm()
    {
        ExecutionCapture.Clear();

        const string instructions = "Verify database migration completed";

        var serverTaskId = await SeedManualInterventionPipelineAsync(
            preStepScript: "echo 'pre'",
            postStepScript: "echo 'post'",
            instructions: instructions);

        DeploymentInterruption capturedInterruption = null;

        await RunPipelineWithInterventionAsync(serverTaskId, "Proceed", onInterruptionFound: interruption =>
        {
            capturedInterruption = interruption;
        });

        capturedInterruption.ShouldNotBeNull();
        capturedInterruption.InterruptionType.ShouldBe(InterruptionType.ManualIntervention);
        capturedInterruption.StepName.ShouldBe("Manual Step");
        capturedInterruption.FormJson.ShouldNotBeNullOrWhiteSpace();
        capturedInterruption.FormJson.ShouldContain(instructions);
        capturedInterruption.Resolution.ShouldBe("Proceed");
        capturedInterruption.ResolvedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task ManualIntervention_HasPendingInterruptionsFlag_SetAndCleared()
    {
        ExecutionCapture.Clear();

        var serverTaskId = await SeedManualInterventionPipelineAsync(
            preStepScript: "echo 'pre'",
            postStepScript: "echo 'post'",
            instructions: "Check flag lifecycle");

        var flagWhilePending = false;

        await RunPipelineWithInterventionAsync(serverTaskId, "Proceed", onBeforeSubmit: async () =>
        {
            await _fixture.Run<IServerTaskDataProvider>(async provider =>
            {
                var task = await provider.GetServerTaskByIdAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
                flagWhilePending = task.HasPendingInterruptions;
            }).ConfigureAwait(false);
        });

        flagWhilePending.ShouldBeTrue("HasPendingInterruptions should be true while interruption is pending");

        await _fixture.Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            task.HasPendingInterruptions.ShouldBeFalse("HasPendingInterruptions should be cleared after submission");
        }).ConfigureAwait(false);
    }

    // ========================================================================
    // Pipeline Runner
    // ========================================================================

    private async Task RunPipelineWithInterventionAsync(int serverTaskId, string decision, Action<DeploymentInterruption> onInterruptionFound = null, Func<Task> onBeforeSubmit = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var pipelineTask = Task.Run(async () =>
        {
            try
            {
                await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
                {
                    await executor.ProcessAsync(serverTaskId, cts.Token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            catch (DeploymentAbortedException)
            {
                // Expected when decision is "Abort" — pipeline records failure and re-throws
            }
        }, cts.Token);

        var submitTask = Task.Run(async () =>
        {
            await PollAndSubmitInterruptionAsync(serverTaskId, decision, onInterruptionFound, onBeforeSubmit, cts.Token).ConfigureAwait(false);
        }, cts.Token);

        await Task.WhenAll(pipelineTask, submitTask).ConfigureAwait(false);
    }

    private async Task PollAndSubmitInterruptionAsync(int serverTaskId, string decision, Action<DeploymentInterruption> onInterruptionFound, Func<Task> onBeforeSubmit, CancellationToken ct)
    {
        DeploymentInterruption interruption = null;

        while (interruption == null)
        {
            ct.ThrowIfCancellationRequested();

            var pending = await _fixture.Run<IDeploymentInterruptionService, List<DeploymentInterruption>>(async service =>
            {
                return await service.GetPendingInterruptionsAsync(serverTaskId, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            interruption = pending?.FirstOrDefault();

            if (interruption == null)
                await Task.Delay(500, ct).ConfigureAwait(false);
        }

        if (onBeforeSubmit != null)
            await onBeforeSubmit().ConfigureAwait(false);

        await _fixture.Run<IDeploymentInterruptionService>(async service =>
        {
            await service.SubmitInterruptionAsync(interruption.Id, new Dictionary<string, string> { ["Result"] = decision }, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (onInterruptionFound != null)
        {
            var resolved = await _fixture.Run<IDeploymentInterruptionService, DeploymentInterruption>(async service =>
            {
                return await service.GetInterruptionByIdAsync(interruption.Id, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            onInterruptionFound(resolved);
        }
    }

    // ========================================================================
    // Seeder
    // ========================================================================

    private async Task<int> SeedManualInterventionPipelineAsync(string preStepScript, string postStepScript, string instructions)
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

            // Step 1 — Run Script (before manual intervention)
            var step1 = await builder.CreateDeploymentStepAsync(process.Id, 1, "Pre Step", "Action", "Success").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step1.Id, ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action1 = await builder.CreateDeploymentActionAsync(step1.Id, 1, "Pre Script", actionType: "Squid.KubernetesRunScript").ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action1.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action1.Id,
                ("Squid.Action.Script.ScriptBody", preStepScript),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // Step 2 — Manual Intervention
            var step2 = await builder.CreateDeploymentStepAsync(process.Id, 2, "Manual Step", "Manual", "Success").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step2.Id, ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action2 = await builder.CreateDeploymentActionAsync(step2.Id, 1, "Manual Intervention Required", actionType: "Squid.Manual").ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action2.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action2.Id,
                ("Squid.Action.Manual.Instructions", instructions)).ConfigureAwait(false);

            // Step 3 — Run Script (after manual intervention)
            var step3 = await builder.CreateDeploymentStepAsync(process.Id, 3, "Post Step", "Action", "Success").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step3.Id, ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action3 = await builder.CreateDeploymentActionAsync(step3.Id, 1, "Post Script", actionType: "Squid.KubernetesRunScript").ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action3.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action3.Id,
                ("Squid.Action.Script.ScriptBody", postStepScript),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // Infrastructure
            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync("E2E Manual Intervention Env").ConfigureAwait(false);

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
                Name = "E2E Manual Intervention Target",
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
                Slug = "e2e-manual-intervention-target"
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var account = new DeploymentAccount
            {
                SpaceId = 1,
                Name = "E2E Manual Intervention Account",
                Slug = "e2e-manual-intervention-account",
                AccountType = AccountType.Token,
                Credentials = DeploymentAccountCredentialsConverter.Serialize(
                    new TokenCredentials { Token = "e2e-test-token" })
            };

            await repository.InsertAsync(account).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "E2E Manual Intervention Deployment",
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
                Name = "E2E Manual Intervention Task",
                Description = "E2E manual intervention test",
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
