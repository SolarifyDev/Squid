using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// Drives the REAL <see cref="ExecuteStepsPhase"/> over a single batch and returns the
/// checkpoint the phase persisted, so tests can assert on what actually reached the
/// checkpoint column rather than on an intermediate the phase might stop using.
///
/// <para>Shared by the seam and accumulation suites: both need the same "run one batch, catch
/// the save" rig, and a single harness keeps them honest about testing the same code path.</para>
/// </summary>
internal static class CheckpointPhaseHarness
{
    internal const int ServerTaskId = 8801;

    /// <param name="emittedOutputs">(name, value) pairs each target emits via a service message.</param>
    /// <param name="perTargetOutputs">Per-target emits, outer index = target. Overrides
    /// <paramref name="emittedOutputs"/> and sets <c>targetCount</c> to its length, so a test can
    /// make different targets emit DIFFERENT values for one name (the collision case).</param>
    /// <param name="seedVariables">Variables already in <c>ctx.Variables</c> before the batch runs.</param>
    /// <param name="restoredOutputVariables">Simulates a resume: outputs recovered from a prior checkpoint.</param>
    /// <param name="targetCount">Number of targets the step runs across (each emits the same outputs).</param>
    internal static async Task<DeploymentExecutionCheckpoint> RunOneBatchAsync(
        IReadOnlyList<(string Name, string Value)> emittedOutputs = null,
        IReadOnlyList<VariableDto> seedVariables = null,
        IReadOnlyList<VariableDto> restoredOutputVariables = null,
        int targetCount = 1,
        IReadOnlyList<IReadOnlyList<(string Name, string Value)>> perTargetOutputs = null)
    {
        if (perTargetOutputs != null) targetCount = perTargetOutputs.Count;
        DeploymentExecutionCheckpoint saved = null;

        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService
            .Setup(s => s.SaveAsync(It.IsAny<DeploymentExecutionCheckpoint>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentExecutionCheckpoint, CancellationToken>((cp, _) =>
            {
                saved = cp;
                return Task.CompletedTask;
            });

        var lifecycle = new DeploymentLifecyclePublisher(System.Array.Empty<IDeploymentLifecycleHandler>());
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == new TrivialHandler());

        var transport = new TestTransport(emittedOutputs ?? System.Array.Empty<(string, string)>(), perTargetOutputs);
        var transportRegistry = new Mock<ITransportRegistry>();
        transportRegistry.Setup(r => r.Resolve(It.IsAny<CommunicationStyle>())).Returns(transport);

        // Identity encryption: these suites are about WHICH variables are checkpointed, not how
        // they are protected. Protection has its own dedicated tests.
        var encryption = new Mock<Squid.Core.Services.Security.IVariableEncryptionService>();
        encryption.Setup(e => e.IsValidEncryptedValue(It.IsAny<string>())).Returns(false);
        encryption.Setup(e => e.EncryptAsync(It.IsAny<string>(), It.IsAny<int>())).Returns<string, int>((v, _) => v);

        var phase = new ExecuteStepsPhase(
            actionHandlerRegistry: registry,
            lifecycle: lifecycle,
            interruptionService: new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object,
            checkpointService: checkpointService.Object,
            serverTaskService: new Mock<IServerTaskService>().Object,
            transportRegistry: transportRegistry.Object,
            externalFeedDataProvider: new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object,
            packageAcquisitionService: new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object,
            serviceMessageParser: new ServiceMessageParser(),
            intentRendererRegistry: Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create(),
            variableEncryptionService: encryption.Object);

        var targets = new List<DeploymentTargetContext>();
        for (var i = 1; i <= targetCount; i++)
        {
            targets.Add(new DeploymentTargetContext
            {
                Machine = new Machine { Id = i, Name = $"test-target-{i}", Roles = JsonSerializer.Serialize(new[] { "web" }) },
                EndpointContext = new EndpointContext { EndpointJson = "{}" },
                Transport = transport,
                CommunicationStyle = transport.CommunicationStyle
            });
        }

        var ctx = new DeploymentTaskContext
        {
            ServerTaskId = ServerTaskId,
            Task = new ServerTaskEntity { Id = ServerTaskId },
            Deployment = new Deployment { Id = 1, EnvironmentId = 1, ChannelId = 1 },
            Release = new ReleaseEntity { Id = 1, Version = "1.0.0" },
            Variables = seedVariables?.ToList() ?? new List<VariableDto>(),
            SelectedPackages = new List<ReleaseSelectedPackage>(),
            RestoredOutputVariables = restoredOutputVariables?.ToList() ?? new List<VariableDto>(),
            AllTargetsContext = targets,
            Steps = new List<DeploymentStepDto>
            {
                new()
                {
                    Id = 1,
                    Name = "OneStep",
                    StepOrder = 1,
                    StartTrigger = string.Empty,
                    Condition = "Success",
                    IsRequired = true,
                    IsDisabled = false,
                    Properties = new List<DeploymentStepPropertyDto>
                    {
                        new() { StepId = 1, PropertyName = SpecialVariables.Step.TargetRoles, PropertyValue = "web" }
                    },
                    Actions = new List<DeploymentActionDto>
                    {
                        new()
                        {
                            Id = 1, Name = "Action", ActionOrder = 1, ActionType = "Squid.Script",
                            IsRequired = true, IsDisabled = false,
                            Properties = new List<DeploymentActionPropertyDto>(),
                            Environments = new List<int>(),
                            ExcludedEnvironments = new List<int>(),
                            Channels = new List<int>()
                        }
                    }
                }
            }
        };

        lifecycle.Initialize(ctx);
        await phase.ExecuteAsync(ctx, CancellationToken.None);

        return saved;
    }

    internal static List<VariableDto> ReadCheckpoint(DeploymentExecutionCheckpoint checkpoint)
        => checkpoint?.OutputVariablesJson == null
            ? new List<VariableDto>()
            : JsonSerializer.Deserialize<List<VariableDto>>(checkpoint.OutputVariablesJson) ?? new List<VariableDto>();

    private sealed class TestTransport(IReadOnlyList<(string Name, string Value)> emitted, IReadOnlyList<IReadOnlyList<(string Name, string Value)>> perTarget) : IDeploymentTransport
    {
        public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesAgent;
        public IEndpointVariableContributor Variables => null;
        public IExecutionStrategy Strategy { get; } = new EmittingStrategy(emitted, perTarget);
        public IHealthCheckStrategy HealthChecker => null;
        public ITransportCapabilities Capabilities { get; } = new TransportCapabilities();
    }

    /// <summary>
    /// Succeeds while emitting the requested output variables via service messages. When
    /// per-target lists are supplied, dispenses them in dispatch order so different targets can
    /// emit DIFFERENT values for one name — the collision shape that exposes append-set bugs.
    /// </summary>
    private sealed class EmittingStrategy(IReadOnlyList<(string Name, string Value)> emitted, IReadOnlyList<IReadOnlyList<(string Name, string Value)>> perTarget) : IExecutionStrategy
    {
        private int _dispatched = -1;

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            var lines = perTarget == null
                ? emitted
                : perTarget[Math.Min(Interlocked.Increment(ref _dispatched), perTarget.Count - 1)];

            return Task.FromResult(new ScriptExecutionResult
            {
                Success = true,
                ExitCode = 0,
                LogLines = lines.Select(e => $"##squid[setVariable name='{e.Name}' value='{e.Value}']").ToList()
            });
        }
    }

    private sealed class TrivialHandler : IActionHandler
    {
        public string ActionType => "Squid.Script";
        public bool CanHandle(DeploymentActionDto action) => true;

        public Task<ExecutionIntent> DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct) =>
            Task.FromResult<ExecutionIntent>(new RunScriptIntent { Name = "trivial", ScriptBody = "echo hi", Syntax = ScriptSyntax.Bash });
    }
}
