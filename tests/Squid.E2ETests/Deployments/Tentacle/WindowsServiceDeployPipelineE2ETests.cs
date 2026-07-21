using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Deployments;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Tentacle;

/// <summary>
/// Contract E2E coverage for Squid.DeployWindowsService. The deployment pipeline
/// fixture keeps the real handler, renderer, variable expansion, lifecycle, and
/// Tentacle transport-capability flow while swapping final host execution for a
/// capturing strategy.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Tier", "Contract")]
public class WindowsServiceDeployPipelineE2ETests
    : IClassFixture<DeploymentPipelineFixture<WindowsServiceDeployPipelineE2ETests>>
{
    private const string ActionName = "Deploy Worker";
    private const string StepName = "Deploy Windows Service";
    private const string TargetRole = "windows-service";

    private readonly DeploymentPipelineFixture<WindowsServiceDeployPipelineE2ETests> _fixture;

    public WindowsServiceDeployPipelineE2ETests(
        DeploymentPipelineFixture<WindowsServiceDeployPipelineE2ETests> fixture)
    {
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    [Theory]
    [InlineData("TentaclePolling")]
    [InlineData("TentacleListening")]
    public async Task FullPipeline_DeployWindowsService_CapturesPowerShellRequestForBothTentacleStyles(string communicationStyle)
    {
        ExecutionCapture.Clear();

        var seed = await SeedWindowsServiceDeploymentAsync(
            communicationStyle,
            variables: null,
            properties: new Dictionary<string, string>
            {
                ["Squid.Action.WindowsService.CreateOrUpdateService"] = "True",
                ["Squid.Action.WindowsService.ServiceName"] = "OrderWorker",
                ["Squid.Action.WindowsService.DisplayName"] = "Order Worker",
                ["Squid.Action.WindowsService.Description"] = "Processes queued orders",
                ["Squid.Action.WindowsService.ExecutablePath"] = "Order.Worker.exe",
                ["Squid.Action.WindowsService.Arguments"] = "--port 9000 --mode prod",
                ["Squid.Action.WindowsService.ServiceAccount"] = "LocalSystem",
                ["Squid.Action.WindowsService.StartMode"] = "Automatic",
                ["Squid.Action.WindowsService.DesiredStatus"] = "Started",
                ["Squid.Action.WindowsService.Dependencies"] = "EventLog",
                ["Squid.Action.WindowsService.Package.ExtractTo"] = $@"C:\Squid\Services\OrderWorker-{Guid.NewGuid():N}",
                ["Squid.Action.WindowsService.Package.PurgeBeforeExtract"] = "True"
            });

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(seed.ServerTaskId, CancellationToken.None);
        });

        await AssertTaskStateAsync(seed.ServerTaskId, TaskState.Success);

        var captured = ExecutionCapture.CapturedRequests.ShouldHaveSingleItem();

        captured.Syntax.ShouldBe(ScriptSyntax.PowerShell);
        captured.StepName.ShouldBe(StepName);
        captured.ActionName.ShouldBe(ActionName);
        captured.StepId.ShouldBe(seed.StepId);
        captured.ActionId.ShouldBe(seed.ActionId);
        captured.Machine.Id.ShouldBe(seed.MachineId);
        captured.Machine.Name.ShouldStartWith($"E2E Windows Service {communicationStyle} Target");
        captured.PackageReferences.ShouldBeEmpty();

        captured.ScriptBody.ShouldContain("# BEGIN GENERATED PREAMBLE (Squid WindowsServiceDeployScriptBuilder)");
        captured.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.WindowsService.ServiceName'] = 'OrderWorker'");
        captured.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.WindowsService.ExecutablePath'] = 'Order.Worker.exe'");
        captured.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.WindowsService.Arguments'] = '--port 9000 --mode prod'");
        captured.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.WindowsService.StartMode'] = 'Automatic'");
        captured.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.WindowsService.DesiredStatus'] = 'Started'");
        captured.ScriptBody.ShouldContain("$SquidSelectedPackages = @()");

        captured.ScriptBody.ShouldContain("function Resolve-PackageRoot");
        captured.ScriptBody.ShouldContain("function Resolve-AcquiredPackageSourcePath");
        captured.ScriptBody.ShouldContain("function Build-BinaryPathName");
        captured.ScriptBody.ShouldContain("Invoke-Sc create");
        captured.ScriptBody.ShouldContain("Invoke-Sc config");
        captured.ScriptBody.ShouldContain("Start-Service");
        captured.ScriptBody.ShouldContain("Stop-ServiceIfRunning");
        captured.ScriptBody.ShouldContain("package-references.json");
    }

    [Theory]
    [InlineData("TentaclePolling")]
    [InlineData("TentacleListening")]
    public async Task FullPipeline_DeployWindowsService_ResolvesVariablesAndMasksSensitivePassword(string communicationStyle)
    {
        ExecutionCapture.Clear();

        const string serviceName = "VariableWorker";
        const string servicePassword = "Super'Secret!";

        var seed = await SeedWindowsServiceDeploymentAsync(
            communicationStyle,
            variables: new[]
            {
                ("WindowsServiceName", serviceName, false),
                ("WindowsServicePort", "9443", false),
                ("WindowsServicePassword", servicePassword, true)
            },
            properties: new Dictionary<string, string>
            {
                ["Squid.Action.WindowsService.CreateOrUpdateService"] = "True",
                ["Squid.Action.WindowsService.ServiceName"] = "#{WindowsServiceName}",
                ["Squid.Action.WindowsService.ExecutablePath"] = "Order.Worker.exe",
                ["Squid.Action.WindowsService.Arguments"] = "--port #{WindowsServicePort}",
                ["Squid.Action.WindowsService.ServiceAccount"] = "SpecificUser",
                ["Squid.Action.WindowsService.CustomAccountName"] = @"DOMAIN\worker",
                ["Squid.Action.WindowsService.CustomAccountPassword"] = "#{WindowsServicePassword}",
                ["Squid.Action.WindowsService.StartMode"] = "Manual",
                ["Squid.Action.WindowsService.DesiredStatus"] = "Stopped"
            });

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(seed.ServerTaskId, CancellationToken.None);
        });

        await AssertTaskStateAsync(seed.ServerTaskId, TaskState.Success);

        var captured = ExecutionCapture.CapturedRequests.ShouldHaveSingleItem();

        captured.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.WindowsService.ServiceName'] = 'VariableWorker'");
        captured.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.WindowsService.Arguments'] = '--port 9443'");
        captured.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.WindowsService.CustomAccountPassword'] = 'Super''Secret!'");
        captured.ScriptBody.ShouldNotContain("#{WindowsServiceName}");
        captured.ScriptBody.ShouldNotContain("#{WindowsServicePort}");
        captured.ScriptBody.ShouldNotContain("#{WindowsServicePassword}");

        captured.Masker.ShouldNotBeNull("Sensitive service-account password must be carried in the script request masker.");
        captured.Masker.Mask($"password={servicePassword}").ShouldBe($"password={SensitiveValueMasker.MaskToken}");
    }

    private async Task<SeededDeployment> SeedWindowsServiceDeploymentAsync(
        string communicationStyle,
        Dictionary<string, string> properties,
        (string Name, string Value, bool IsSensitive)[] variables)
    {
        SeededDeployment result = null;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            if (variables != null)
            {
                foreach (var variable in variables)
                    await builder.CreateVariableAsync(variableSet.Id, variable.Name, variable.Value, isSensitive: variable.IsSensitive).ConfigureAwait(false);
            }

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, StepName).ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id, (SpecialVariables.Step.TargetRoles, TargetRole)).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id,
                1,
                ActionName,
                actionType: SpecialVariables.ActionTypes.DeployWindowsService).ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action.Id, TargetRole).ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id, properties.Select(p => (p.Key, p.Value)).ToArray()).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync($"E2E Windows Service {communicationStyle} Env {suffix}").ConfigureAwait(false);
            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var endpointJson = BuildTentacleEndpointJson(communicationStyle, suffix);
            var machine = new Machine
            {
                Name = $"E2E Windows Service {communicationStyle} Target {suffix}",
                IsDisabled = false,
                Roles = DeploymentTargetFinder.SerializeRoles(new[] { TargetRole }),
                EnvironmentIds = DeploymentTargetFinder.SerializeIds(new[] { environment.Id }),
                Endpoint = endpointJson,
                SpaceId = 1,
                Slug = $"e2e-windows-service-{communicationStyle.ToLowerInvariant()}-{suffix}"
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var deployment = await CreateDeploymentAsync(repository, unitOfWork, project, channel, environment, release, suffix).ConfigureAwait(false);
            var serverTask = await CreateServerTaskAsync(repository, unitOfWork, project, environment, suffix).ConfigureAwait(false);

            deployment.TaskId = serverTask.Id;
            await repository.UpdateAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            result = new SeededDeployment(serverTask.Id, machine.Id, step.Id, action.Id);
        }).ConfigureAwait(false);

        return result!;
    }

    private static string BuildTentacleEndpointJson(string communicationStyle, string suffix)
    {
        return communicationStyle == "TentaclePolling"
            ? JsonSerializer.Serialize(new
            {
                CommunicationStyle = "TentaclePolling",
                SubscriptionId = $"windows-service-e2e-sub-{suffix}",
                Thumbprint = $"WINDOWS-SERVICE-E2E-POLLING-{suffix}"
            })
            : JsonSerializer.Serialize(new
            {
                CommunicationStyle = "TentacleListening",
                Uri = $"https://localhost:10933/{suffix}",
                Thumbprint = $"WINDOWS-SERVICE-E2E-LISTENING-{suffix}"
            });
    }

    private static async Task<Deployment> CreateDeploymentAsync(
        IRepository repository,
        IUnitOfWork unitOfWork,
        Project project,
        Channel channel,
        Environment environment,
        Release release,
        string suffix)
    {
        var deployment = new Deployment
        {
            Name = $"E2E Windows Service Deployment {suffix}",
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
        return deployment;
    }

    private static async Task<ServerTask> CreateServerTaskAsync(
        IRepository repository,
        IUnitOfWork unitOfWork,
        Project project,
        Environment environment,
        string suffix)
    {
        var serverTask = new ServerTask
        {
            Name = $"E2E Windows Service Task {suffix}",
            Description = "E2E Windows service deploy contract",
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
        return serverTask;
    }

    private async Task AssertTaskStateAsync(int serverTaskId, string expectedState)
    {
        await _fixture.Run<IServerTaskDataProvider>(async taskDataProvider =>
        {
            var task = await taskDataProvider.GetServerTaskByIdAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);

            task.ShouldNotBeNull();
            task.State.ShouldBe(expectedState, $"Expected task {serverTaskId} state '{expectedState}' but was '{task.State}'");
        }).ConfigureAwait(false);
    }

    private sealed record SeededDeployment(int ServerTaskId, int MachineId, int StepId, int ActionId);
}
