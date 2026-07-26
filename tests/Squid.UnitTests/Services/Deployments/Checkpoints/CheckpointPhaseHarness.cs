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

    /// <summary>Prefix the faithful encryption mock stamps onto protected values.</summary>
    internal const string EncryptedPrefix = "ENC(";

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
        IReadOnlyList<IReadOnlyList<(string Name, string Value)>> perTargetOutputs = null,
        bool faithfulEncryption = false,
        List<DeploymentExecutionCheckpoint> allSaves = null,
        IReadOnlyList<(string Name, string Value)> emittedSensitiveOutputs = null,
        int stepCount = 1,
        List<string> encryptCalls = null)
    {
        if (perTargetOutputs != null) targetCount = perTargetOutputs.Count;

        DeploymentExecutionCheckpoint saved = null;

        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService
            .Setup(s => s.SaveAsync(It.IsAny<DeploymentExecutionCheckpoint>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentExecutionCheckpoint, CancellationToken>((cp, _) =>
            {
                saved = cp;
                // Clone: the phase reuses one entity instance per save, so a caller collecting
                // the sequence would otherwise see N references to the final state.
                allSaves?.Add(new DeploymentExecutionCheckpoint
                {
                    ServerTaskId = cp.ServerTaskId,
                    DeploymentId = cp.DeploymentId,
                    LastCompletedBatchIndex = cp.LastCompletedBatchIndex,
                    FailureEncountered = cp.FailureEncountered,
                    OutputVariablesJson = cp.OutputVariablesJson,
                    BatchStatesJson = cp.BatchStatesJson
                });
                return Task.CompletedTask;
            });

        var lifecycle = new DeploymentLifecyclePublisher(System.Array.Empty<IDeploymentLifecycleHandler>());
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == new TrivialHandler());

        var transport = new TestTransport(emittedOutputs ?? System.Array.Empty<(string, string)>(), perTargetOutputs, emittedSensitiveOutputs);
        var transportRegistry = new Mock<ITransportRegistry>();
        transportRegistry.Setup(r => r.Resolve(It.IsAny<CommunicationStyle>())).Returns(transport);

        // Identity encryption by default: most suites here are about WHICH variables are
        // checkpointed, not how they are protected. faithfulEncryption swaps in a mock that
        // actually transforms and recognises its own output, which is what makes the
        // protect-at-capture wiring observable — under the identity mock, removing protection
        // entirely is invisible.
        var encryption = new Mock<Squid.Core.Services.Security.IVariableEncryptionService>();

        if (faithfulEncryption)
        {
            encryption.Setup(e => e.IsValidEncryptedValue(It.IsAny<string>()))
                .Returns<string>(v => v != null && v.StartsWith(EncryptedPrefix, StringComparison.Ordinal));
            // Base64 so the ciphertext does NOT contain the plaintext as a substring — a
            // prefix-only fake would let "value never appears in plaintext" assertions pass
            // even when protection was skipped.
            encryption.Setup(e => e.EncryptAsync(It.IsAny<string>(), It.IsAny<int>()))
                .Returns<string, int>((v, scope) =>
                {
                    encryptCalls?.Add(v);
                    return EncryptedPrefix + scope + ":" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(v ?? string.Empty));
                });
        }
        else
        {
            encryption.Setup(e => e.IsValidEncryptedValue(It.IsAny<string>())).Returns(false);
            encryption.Setup(e => e.EncryptAsync(It.IsAny<string>(), It.IsAny<int>()))
                .Returns<string, int>((v, _) => { encryptCalls?.Add(v); return v; });
        }

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
            // Restored outputs appear in BOTH places on a real resume: PrepareDeploymentPhase
            // merges them into the live list (so later steps resolve them) and ExecuteStepsPhase
            // re-seeds the captured set from RestoredOutputVariables. This harness runs only the
            // execute phase, so it must stage the live-list half itself — without it the merge
            // treats a same-value re-emit as brand new and appends a duplicate the real pipeline
            // would have skipped, quietly making re-emit assertions meaningless.
            Variables = (seedVariables ?? Enumerable.Empty<VariableDto>())
                .Concat(restoredOutputVariables ?? Enumerable.Empty<VariableDto>())
                .ToList(),
            SelectedPackages = new List<ReleaseSelectedPackage>(),
            RestoredOutputVariables = restoredOutputVariables?.ToList() ?? new List<VariableDto>(),
            AllTargetsContext = targets,
            Steps = BuildSteps(stepCount)
        };

        lifecycle.Initialize(ctx);
        await phase.ExecuteAsync(ctx, CancellationToken.None);

        return saved;
    }

    /// <summary>
    /// N sequential steps (empty StartTrigger => each forms its own batch, so each produces its
    /// own checkpoint write). Every step emits the same outputs via the shared strategy.
    /// </summary>
    private static List<DeploymentStepDto> BuildSteps(int stepCount)
        => Enumerable.Range(1, stepCount).Select(i => new DeploymentStepDto
        {
            Id = i,
            Name = stepCount == 1 ? "OneStep" : $"Step{i}",
            StepOrder = i,
            StartTrigger = string.Empty,
            Condition = "Success",
            IsRequired = true,
            IsDisabled = false,
            Properties = new List<DeploymentStepPropertyDto>
            {
                new() { StepId = i, PropertyName = SpecialVariables.Step.TargetRoles, PropertyValue = "web" }
            },
            Actions = new List<DeploymentActionDto>
            {
                new()
                {
                    Id = i, Name = $"Action{i}", ActionOrder = 1, ActionType = "Squid.Script",
                    IsRequired = true, IsDisabled = false,
                    Properties = new List<DeploymentActionPropertyDto>(),
                    Environments = new List<int>(),
                    ExcludedEnvironments = new List<int>(),
                    Channels = new List<int>()
                }
            }
        }).ToList();

    internal static List<VariableDto> ReadCheckpoint(DeploymentExecutionCheckpoint checkpoint)
        => checkpoint?.OutputVariablesJson == null
            ? new List<VariableDto>()
            : JsonSerializer.Deserialize<List<VariableDto>>(checkpoint.OutputVariablesJson) ?? new List<VariableDto>();

    private sealed class TestTransport(IReadOnlyList<(string Name, string Value)> emitted, IReadOnlyList<IReadOnlyList<(string Name, string Value)>> perTarget, IReadOnlyList<(string Name, string Value)> sensitive) : IDeploymentTransport
    {
        public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesAgent;
        public IEndpointVariableContributor Variables => null;
        public IExecutionStrategy Strategy { get; } = new EmittingStrategy(emitted, perTarget, sensitive);
        public IHealthCheckStrategy HealthChecker => null;
        public ITransportCapabilities Capabilities { get; } = new TransportCapabilities();
    }

    /// <summary>
    /// Succeeds while emitting the requested output variables via service messages. When
    /// per-target lists are supplied, dispenses them in dispatch order so different targets can
    /// emit DIFFERENT values for one name — the collision shape that exposes append-set bugs.
    /// </summary>
    private sealed class EmittingStrategy(IReadOnlyList<(string Name, string Value)> emitted, IReadOnlyList<IReadOnlyList<(string Name, string Value)>> perTarget, IReadOnlyList<(string Name, string Value)> sensitive) : IExecutionStrategy
    {
        private int _dispatched = -1;

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            var lines = perTarget == null
                ? emitted
                : perTarget[Math.Min(Interlocked.Increment(ref _dispatched), perTarget.Count - 1)];

            var logLines = lines.Select(e => $"##squid[setVariable name='{e.Name}' value='{e.Value}']").ToList();

            if (sensitive != null)
                logLines.AddRange(sensitive.Select(e => $"##squid[setVariable name='{e.Name}' value='{e.Value}' sensitive='True']"));

            return Task.FromResult(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = logLines });
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
