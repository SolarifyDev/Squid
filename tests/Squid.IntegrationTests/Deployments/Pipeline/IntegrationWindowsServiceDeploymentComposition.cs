using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Deployments.Pipeline;

public class IntegrationWindowsServiceDeploymentComposition : DeploymentFixtureBase
{
    [Fact]
    public async Task ProcessAsync_WindowsServiceAction_ComposesRenderedTentacleRequest()
    {
        var capture = new CapturingExecutionStrategy();
        var packageAcquisition = new CapturingPackageAcquisitionService(new PackageAcquisitionResult(
            "/tmp/squid-packages/Order.Worker.2.3.4.nupkg",
            "Order.Worker",
            "2.3.4",
            4096,
            "ABCDEF0123456789"));
        var capabilities = new InMemoryMachineRuntimeCapabilitiesCache();
        var seed = await SeedWindowsServiceDeploymentAsync().ConfigureAwait(false);

        capabilities.Store(seed.MachineId, new Dictionary<string, string>
        {
            ["os"] = AgentOperatingSystems.Windows,
            ["installedShells"] = "powershell,pwsh,cmd",
            ["defaultShell"] = "powershell"
        }, agentVersion: "9.0.0");

        await Run<IDeploymentTaskExecutor>(
            executor => executor.ProcessAsync(seed.ServerTaskId, CancellationToken.None),
            builder => RegisterExecutionOverrides(builder, capture, packageAcquisition, capabilities)).ConfigureAwait(false);

        await AssertTaskStateAsync(seed.ServerTaskId, TaskState.Success).ConfigureAwait(false);

        packageAcquisition.Calls.Count.ShouldBe(1,
            customMessage: "The selected package must inject and execute the synthetic Acquire Packages step before the Windows service step.");
        packageAcquisition.Calls[0].FeedId.ShouldBe(seed.FeedId);
        packageAcquisition.Calls[0].PackageId.ShouldBe("Order.Worker");
        packageAcquisition.Calls[0].Version.ShouldBe("2.3.4");
        packageAcquisition.Calls[0].DeploymentId.ShouldBe(seed.DeploymentId);

        var request = capture.CapturedRequests.ShouldHaveSingleItem();

        request.StepName.ShouldBe("Deploy Windows Service");
        request.ActionName.ShouldBe("Deploy Worker");
        request.StepId.ShouldBe(seed.StepId);
        request.ActionId.ShouldBe(seed.ActionId);
        request.Syntax.ShouldBe(ScriptSyntax.PowerShell);
        request.Machine.Id.ShouldBe(seed.MachineId);
        request.PackageReferences.ShouldHaveSingleItem().ShouldBe(packageAcquisition.Result);

        request.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.WindowsService.ServiceName'] = 'OrderWorker'");
        request.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.WindowsService.Arguments'] = '--port 9000 --mode prod'");
        request.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.WindowsService.CustomAccountPassword'] = 'Super''Secret!'");
        request.ScriptBody.ShouldContain("PackageReferenceName = 'Order.Worker'");
        request.ScriptBody.ShouldContain("Version = '2.3.4'");
        request.ScriptBody.ShouldContain("function Resolve-PackageRoot");
        request.ScriptBody.ShouldNotContain("#{WindowsServiceName}");
        request.ScriptBody.ShouldNotContain("#{WindowsServicePort}");
        request.ScriptBody.ShouldNotContain("#{WindowsServicePassword}");

        request.Masker.ShouldNotBeNull("Sensitive service-account password must be carried in the script request masker.");
        request.Masker.Mask($"password={seed.ServicePassword}").ShouldBe($"password={SensitiveValueMasker.MaskToken}");
    }

    private void RegisterExecutionOverrides(
        ContainerBuilder builder,
        CapturingExecutionStrategy capture,
        CapturingPackageAcquisitionService packageAcquisition,
        IMachineRuntimeCapabilitiesCache capabilities)
    {
        builder.RegisterInstance(capabilities)
            .As<IMachineRuntimeCapabilitiesCache>()
            .SingleInstance();

        builder.RegisterInstance(packageAcquisition)
            .As<IPackageAcquisitionService>()
            .SingleInstance();

        builder.Register(ctx => new CapturingTentacleTransportRegistry(capabilities, capture))
            .As<ITransportRegistry>()
            .InstancePerLifetimeScope();
    }

    private async Task<SeededDeployment> SeedWindowsServiceDeploymentAsync()
    {
        SeededDeployment? result = null;
        const string servicePassword = "Super'Secret!";

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);
            await builder.CreateVariableAsync(variableSet.Id, "WindowsServiceName", "OrderWorker").ConfigureAwait(false);
            await builder.CreateVariableAsync(variableSet.Id, "WindowsServicePort", "9000").ConfigureAwait(false);
            await builder.CreateVariableAsync(variableSet.Id, "WindowsServicePassword", servicePassword, isSensitive: true).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Windows Service").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id, (SpecialVariables.Step.TargetRoles, "windows-service")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id,
                1,
                "Deploy Worker",
                actionType: SpecialVariables.ActionTypes.DeployWindowsService).ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action.Id, "windows-service").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id,
                ("Squid.Action.WindowsService.CreateOrUpdateService", "True"),
                ("Squid.Action.WindowsService.ServiceName", "#{WindowsServiceName}"),
                ("Squid.Action.WindowsService.DisplayName", "Order Worker"),
                ("Squid.Action.WindowsService.Description", "Processes queued orders"),
                ("Squid.Action.WindowsService.ExecutablePath", "Order.Worker.exe"),
                ("Squid.Action.WindowsService.Arguments", "--port #{WindowsServicePort} --mode prod"),
                ("Squid.Action.WindowsService.ServiceAccount", "SpecificUser"),
                ("Squid.Action.WindowsService.CustomAccountName", @"DOMAIN\order-worker"),
                ("Squid.Action.WindowsService.CustomAccountPassword", "#{WindowsServicePassword}"),
                ("Squid.Action.WindowsService.StartMode", "Automatic"),
                ("Squid.Action.WindowsService.DesiredStatus", "Started"),
                ("Squid.Action.WindowsService.Dependencies", "EventLog"),
                ("Squid.Action.WindowsService.Package.ExtractTo", $@"C:\Squid\Services\OrderWorker-{suffix}"),
                ("Squid.Action.WindowsService.Package.PurgeBeforeExtract", "True")).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync($"Windows Service Env {suffix}").ConfigureAwait(false);
            var feed = await CreateFeedAsync(repository, unitOfWork, suffix).ConfigureAwait(false);
            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            await repository.InsertAsync(new ReleaseSelectedPackage
            {
                ReleaseId = release.Id,
                FeedId = feed.Id,
                ActionName = "Deploy Worker",
                PackageReferenceName = "Order.Worker",
                Version = "2.3.4"
            }).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var endpointJson = JsonSerializer.Serialize(new
            {
                CommunicationStyle = "TentaclePolling",
                SubscriptionId = $"windows-service-sub-{suffix}",
                Thumbprint = $"WINDOWS-SERVICE-THUMBPRINT-{suffix}"
            });

            var machine = new Machine
            {
                Name = $"Windows Service Target {suffix}",
                IsDisabled = false,
                Roles = DeploymentTargetFinder.SerializeRoles(new[] { "windows-service" }),
                EnvironmentIds = DeploymentTargetFinder.SerializeIds(new[] { environment.Id }),
                Endpoint = endpointJson,
                SpaceId = 1,
                Slug = $"windows-service-target-{suffix}"
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var deployment = await CreateDeploymentAsync(repository, unitOfWork, project, channel, environment, release, suffix).ConfigureAwait(false);
            var serverTask = await CreateServerTaskAsync(repository, unitOfWork, project, environment, suffix).ConfigureAwait(false);

            deployment.TaskId = serverTask.Id;
            await repository.UpdateAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            result = new SeededDeployment(serverTask.Id, deployment.Id, machine.Id, step.Id, action.Id, feed.Id, servicePassword);
        }).ConfigureAwait(false);

        return result!;
    }

    private static async Task<ExternalFeed> CreateFeedAsync(IRepository repository, IUnitOfWork unitOfWork, string suffix)
    {
        var feed = new ExternalFeed
        {
            FeedType = "NuGet",
            Properties = "{}",
            FeedUri = "https://packages.example.test/nuget",
            Username = string.Empty,
            Password = string.Empty,
            Name = $"Windows Service Feed {suffix}",
            Slug = $"windows-service-feed-{suffix}",
            PackageAcquisitionLocationOptions = string.Empty,
            SpaceId = 1,
            CreatedDate = DateTimeOffset.UtcNow,
            CreatedBy = 0,
            LastModifiedDate = DateTimeOffset.UtcNow,
            LastModifiedBy = 0
        };

        await repository.InsertAsync(feed).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        return feed;
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
            Name = $"Windows Service Deployment {suffix}",
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
            Name = $"Windows Service Task {suffix}",
            Description = "Integration Windows service deploy",
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
        await Run<IServerTaskDataProvider>(async taskDataProvider =>
        {
            var task = await taskDataProvider.GetServerTaskByIdAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);

            task.ShouldNotBeNull();
            task.State.ShouldBe(expectedState, $"Expected task {serverTaskId} state '{expectedState}' but was '{task.State}'");
        }).ConfigureAwait(false);
    }

    private sealed record SeededDeployment(
        int ServerTaskId,
        int DeploymentId,
        int MachineId,
        int StepId,
        int ActionId,
        int FeedId,
        string ServicePassword);

    private sealed class CapturingExecutionStrategy : IExecutionStrategy
    {
        private readonly List<ScriptExecutionRequest> _capturedRequests = new();

        public IReadOnlyList<ScriptExecutionRequest> CapturedRequests => _capturedRequests;

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            _capturedRequests.Add(request);

            return Task.FromResult(new ScriptExecutionResult
            {
                Success = true,
                ExitCode = 0,
                LogLines = new List<string>()
            });
        }
    }

    private sealed class CapturingPackageAcquisitionService : IPackageAcquisitionService
    {
        public CapturingPackageAcquisitionService(PackageAcquisitionResult result) => Result = result;

        public PackageAcquisitionResult Result { get; }
        public List<PackageAcquisitionCall> Calls { get; } = new();

        public Task<PackageAcquisitionResult> AcquireAsync(ExternalFeed feed, string packageId, string version, int deploymentId, CancellationToken ct)
        {
            Calls.Add(new PackageAcquisitionCall(feed.Id, packageId, version, deploymentId));
            return Task.FromResult(Result);
        }
    }

    private sealed record PackageAcquisitionCall(int FeedId, string PackageId, string Version, int DeploymentId);

    private sealed class CapturingTentacleTransportRegistry : ITransportRegistry
    {
        private readonly Dictionary<CommunicationStyle, IDeploymentTransport> _transports;

        public CapturingTentacleTransportRegistry(IMachineRuntimeCapabilitiesCache capabilities, IExecutionStrategy strategy)
        {
            _transports = new Dictionary<CommunicationStyle, IDeploymentTransport>
            {
                [CommunicationStyle.TentaclePolling] = new CapturingTentacleTransport(
                    CommunicationStyle.TentaclePolling,
                    new TentacleEndpointVariableContributor(capabilities),
                    strategy,
                    TentaclePollingTransport.Capability),
                [CommunicationStyle.TentacleListening] = new CapturingTentacleTransport(
                    CommunicationStyle.TentacleListening,
                    new TentacleEndpointVariableContributor(capabilities),
                    strategy,
                    TentacleListeningTransport.Capability)
            };
        }

        public IDeploymentTransport Resolve(CommunicationStyle style)
            => _transports.TryGetValue(style, out var transport) ? transport : null;
    }

    private sealed class CapturingTentacleTransport : DeploymentTransport
    {
        public CapturingTentacleTransport(
            CommunicationStyle communicationStyle,
            IEndpointVariableContributor variables,
            IExecutionStrategy strategy,
            ITransportCapabilities capabilities)
            : base(communicationStyle, variables, strategy, capabilities)
        {
        }
    }
}
