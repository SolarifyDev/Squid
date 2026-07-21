using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Deployments.Pipeline;

public class IntegrationWindowsServiceCapabilityFiltering : DeploymentFixtureBase
{
    [Fact]
    public async Task ProcessAsync_WindowsServiceOnNonWindowsTarget_EmitsCapabilityFilteredEventAndDoesNotExecute()
    {
        var capture = new CapturingExecutionStrategy();
        var events = new CapturingLifecycleHandler();
        var capabilities = new InMemoryMachineRuntimeCapabilitiesCache();
        var seed = await SeedWindowsServiceDeploymentAsync().ConfigureAwait(false);

        capabilities.Store(seed.MachineId, new Dictionary<string, string>
        {
            ["os"] = AgentOperatingSystems.Linux,
            ["installedShells"] = "powershell,pwsh,cmd",
            ["defaultShell"] = "powershell"
        }, agentVersion: "9.0.0");

        await Run<IDeploymentTaskExecutor>(
            executor => executor.ProcessAsync(seed.ServerTaskId, CancellationToken.None),
            builder => RegisterExecutionOverrides(builder, capture, events, capabilities)).ConfigureAwait(false);

        await AssertTaskStateAsync(seed.ServerTaskId, TaskState.Success).ConfigureAwait(false);

        capture.Calls.ShouldBe(0,
            customMessage: "Capability-filtered Windows service dispatches must be skipped before rendering/execution.");

        var filtered = events.Events.OfType<ActionCapabilityFilteredEvent>().ShouldHaveSingleItem();
        filtered.Context.ActionName.ShouldBe("Deploy Worker");
        filtered.Context.ActionType.ShouldBe(SpecialVariables.ActionTypes.DeployWindowsService);
        filtered.Context.MachineName.ShouldBe("Linux Tentacle Target");
        filtered.Context.CommunicationStyle.ShouldBe(CommunicationStyle.TentaclePolling);
        filtered.Context.Message.ShouldContain(CapabilityKeys.OsSlot);

        events.Events.OfType<ActionRunningEvent>().ShouldBeEmpty();
        events.Events.OfType<ActionExecutingEvent>().ShouldBeEmpty();

        var completed = events.Events.OfType<StepCompletedEvent>()
            .Where(e => e.Context.StepName == "Deploy Windows Service")
            .ShouldHaveSingleItem();
        completed.Context.Failed.ShouldBeFalse();
    }

    private static void RegisterExecutionOverrides(
        ContainerBuilder builder,
        CapturingExecutionStrategy capture,
        CapturingLifecycleHandler events,
        IMachineRuntimeCapabilitiesCache capabilities)
    {
        builder.RegisterInstance(capabilities)
            .As<IMachineRuntimeCapabilitiesCache>()
            .SingleInstance();

        builder.RegisterInstance(events)
            .As<IDeploymentLifecycleHandler>()
            .SingleInstance();

        builder.Register(ctx => new CapturingTentacleTransportRegistry(capabilities, capture))
            .As<ITransportRegistry>()
            .InstancePerLifetimeScope();
    }

    private async Task<SeededDeployment> SeedWindowsServiceDeploymentAsync()
    {
        SeededDeployment? result = null;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

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
                ("Squid.Action.WindowsService.ServiceName", "OrderWorker"),
                ("Squid.Action.WindowsService.ExecutablePath", "Order.Worker.exe"),
                ("Squid.Action.WindowsService.StartMode", "Automatic"),
                ("Squid.Action.WindowsService.DesiredStatus", "Started")).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync($"Windows Service Filter Env {suffix}").ConfigureAwait(false);
            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var endpointJson = JsonSerializer.Serialize(new
            {
                CommunicationStyle = "TentaclePolling",
                SubscriptionId = $"linux-service-sub-{suffix}",
                Thumbprint = $"LINUX-SERVICE-THUMBPRINT-{suffix}"
            });

            var machine = new Machine
            {
                Name = "Linux Tentacle Target",
                IsDisabled = false,
                Roles = DeploymentTargetFinder.SerializeRoles(new[] { "windows-service" }),
                EnvironmentIds = DeploymentTargetFinder.SerializeIds(new[] { environment.Id }),
                Endpoint = endpointJson,
                SpaceId = 1,
                Slug = $"linux-service-target-{suffix}"
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = $"Windows Service Filter Deployment {suffix}",
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
                Name = $"Windows Service Filter Task {suffix}",
                Description = "Integration Windows service capability filter",
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

            result = new SeededDeployment(serverTask.Id, machine.Id);
        }).ConfigureAwait(false);

        return result!;
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

    private sealed record SeededDeployment(int ServerTaskId, int MachineId);

    private sealed class CapturingLifecycleHandler : IDeploymentLifecycleHandler
    {
        public int Order => int.MaxValue;
        public List<DeploymentLifecycleEvent> Events { get; } = new();

        public void Initialize(DeploymentTaskContext ctx) { }

        public Task HandleAsync(DeploymentLifecycleEvent @event, CancellationToken ct)
        {
            Events.Add(@event);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingExecutionStrategy : IExecutionStrategy
    {
        public int Calls { get; private set; }

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            Calls++;

            return Task.FromResult(new ScriptExecutionResult
            {
                Success = true,
                ExitCode = 0,
                LogLines = new List<string>()
            });
        }
    }

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
