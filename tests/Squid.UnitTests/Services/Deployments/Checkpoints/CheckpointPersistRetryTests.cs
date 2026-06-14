using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Xunit;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// P0-5 — pins that <c>ExecuteStepsPhase.PersistCheckpointAsync</c> retries
/// transient DB write failures with exponential backoff before giving up.
///
/// <para><b>The bug it closes</b>: pre-fix the persist path was a single
/// attempt; on transient failure (DB connection blip, brief lock contention)
/// the catch block logged a Warning and continued. The Warning-level meant
/// operators running production logs at Error threshold never saw the loss;
/// a server restart at the wrong moment would replay batches the agent had
/// already completed, doubling up side effects (kubectl apply ×N, helm
/// upgrade ×N, etc.).</para>
///
/// <para><b>The fix</b>: 3 attempts with exponential backoff
/// (200ms / 600ms / 1800ms). On final failure escalate to Log.Error so
/// alerting at Error threshold catches it; deploy still continues because
/// the workload itself is fine — only the resume story is degraded.</para>
/// </summary>
public sealed class CheckpointPersistRetryTests
{
    private const int TestServerTaskId = 9999;

    [Fact]
    public void MaxAttempts_PinnedAt3()
    {
        // Operators running on slow / saturated DBs may want to bump this.
        // Pin so the rename / value change is a deliberate, code-reviewed
        // decision — silently lowering would weaken the resume guarantee.
        ExecuteStepsPhase.CheckpointPersistMaxAttempts.ShouldBe(3,
            customMessage: "Lowering this past 3 weakens transient-failure recovery; " +
                          "raising it past ~5 starts blocking deploys on permanent failures.");
    }

    [Fact]
    public void InitialDelay_IsAt200ms()
    {
        ExecuteStepsPhase.CheckpointPersistInitialDelay.TotalMilliseconds.ShouldBe(200,
            customMessage: "200 ms × 3^N covers most transient DB blips (connection drop, " +
                          "brief lock wait) without blocking steady-state deploys.");
    }

    [Fact]
    public async Task PersistCheckpoint_TransientFailureThenSuccess_RetriesAndSucceeds()
    {
        // The first call throws; the second succeeds. PersistCheckpointAsync
        // must retry exactly once (not give up after attempt 1).
        var attemptCount = 0;
        DeploymentExecutionCheckpoint savedCheckpoint = null;

        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService
            .Setup(s => s.SaveAsync(It.IsAny<DeploymentExecutionCheckpoint>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentExecutionCheckpoint, CancellationToken>((cp, _) =>
            {
                attemptCount++;
                if (attemptCount == 1)
                    throw new InvalidOperationException("transient DB blip");
                savedCheckpoint = cp;
                return Task.CompletedTask;
            });

        await DriveOneBatchAsync(checkpointService.Object);

        attemptCount.ShouldBe(2,
            customMessage: "Must retry exactly once (1 fail + 1 success = 2 attempts). " +
                          "If 1: retry policy didn't run. If 3: retried after the success.");
        savedCheckpoint.ShouldNotBeNull("the second attempt's checkpoint must have been persisted");
    }

    [Fact]
    public async Task PersistCheckpoint_AllAttemptsFail_LogsErrorAndContinues()
    {
        // All 3 attempts fail. Phase must NOT throw — deploy continues.
        // We verify the retry happened by counting attempts.
        var attemptCount = 0;

        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService
            .Setup(s => s.SaveAsync(It.IsAny<DeploymentExecutionCheckpoint>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                attemptCount++;
                throw new InvalidOperationException($"persistent DB failure (attempt {attemptCount})");
            });

        // Should not throw — deploy must continue when persist fails after retries.
        await Should.NotThrowAsync(() => DriveOneBatchAsync(checkpointService.Object));

        attemptCount.ShouldBe(ExecuteStepsPhase.CheckpointPersistMaxAttempts,
            customMessage: $"All {ExecuteStepsPhase.CheckpointPersistMaxAttempts} attempts must have run before giving up. " +
                          "Fewer attempts = retry exited early; more = retry exceeded its cap.");
    }

    [Fact]
    public async Task PersistCheckpoint_CancellationDuringRetry_StopsImmediately()
    {
        // If the deploy CT is cancelled mid-retry (e.g. operator hit "stop"),
        // we must NOT keep blocking on Task.Delay through the remaining
        // backoff — the cancellation has to propagate cleanly to the
        // pipeline runner's cancel-vs-failure precedence logic.
        var cts = new CancellationTokenSource();
        var attemptCount = 0;

        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService
            .Setup(s => s.SaveAsync(It.IsAny<DeploymentExecutionCheckpoint>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                attemptCount++;
                if (attemptCount == 1)
                {
                    // Cancel just before the retry's Task.Delay would start.
                    cts.Cancel();
                }
                throw new InvalidOperationException("transient");
            });

        await Should.ThrowAsync<OperationCanceledException>(
            () => DriveOneBatchAsync(checkpointService.Object, cts.Token));
    }

    [Fact]
    public async Task PersistCheckpoint_RetryDelays_AreBoundedAndExponential()
    {
        // Stopwatch the full retry cycle on permanent failure. Expected total
        // delay: 200 + 600 = 800 ms (delays between attempts 1→2 and 2→3).
        // Bound at 5 s to avoid flakiness on heavily-loaded CI.
        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService
            .Setup(s => s.SaveAsync(It.IsAny<DeploymentExecutionCheckpoint>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("always fails"));

        var sw = Stopwatch.StartNew();
        await DriveOneBatchAsync(checkpointService.Object);
        sw.Stop();

        sw.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(700),
            customMessage: "Total retry delay must be at least 700ms (200ms + 600ms backoff sum) — " +
                          "anything less suggests the retry didn't actually wait.");
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5),
            customMessage: "Total retry should be well under 5s; longer indicates an unbounded backoff.");
    }

    [Fact]
    public async Task EnsureCheckpointRow_TransientFailureThenSuccess_Retries()
    {
        // M2: the up-front ensure-row write (resume-by-ticket substrate) must
        // retry transient failures like the batch-boundary persist does — a
        // single blip must not silently disable resume-by-ticket for the run.
        var ensureAttempts = 0;

        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService
            .Setup(s => s.EnsureExistsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                ensureAttempts++;
                if (ensureAttempts == 1)
                    throw new InvalidOperationException("transient DB blip");
                return Task.CompletedTask;
            });
        checkpointService
            .Setup(s => s.SaveAsync(It.IsAny<DeploymentExecutionCheckpoint>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await DriveOneBatchAsync(checkpointService.Object);

        ensureAttempts.ShouldBe(2,
            customMessage: "EnsureCheckpointRowAsync must retry (1 fail + 1 success); fewer = no retry, more = over-retry.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Run ExecuteStepsPhase against a single trivial step that succeeds, so
    /// the post-batch persist path runs exactly once with the supplied
    /// checkpoint service.
    /// </summary>
    private static async Task DriveOneBatchAsync(IDeploymentCheckpointService checkpointService, CancellationToken ct = default)
    {
        var lifecycle = new DeploymentLifecyclePublisher(System.Array.Empty<IDeploymentLifecycleHandler>());
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == new TrivialHandler());

        var transport = new TestTransport();
        var transportRegistry = new Mock<ITransportRegistry>();
        transportRegistry.Setup(r => r.Resolve(It.IsAny<CommunicationStyle>())).Returns(transport);

        var encryption = new Mock<Squid.Core.Services.Security.IVariableEncryptionService>();
        encryption.Setup(e => e.IsValidEncryptedValue(It.IsAny<string>())).Returns(false);
        encryption.Setup(e => e.EncryptAsync(It.IsAny<string>(), It.IsAny<int>())).Returns<string, int>((v, _) => v);

        var phase = new ExecuteStepsPhase(
            actionHandlerRegistry: registry,
            lifecycle: lifecycle,
            interruptionService: new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object,
            checkpointService: checkpointService,
            serverTaskService: new Mock<IServerTaskService>().Object,
            transportRegistry: transportRegistry.Object,
            externalFeedDataProvider: new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object,
            packageAcquisitionService: new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object,
            serviceMessageParser: new ServiceMessageParser(),
            intentRendererRegistry: Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create(),
            variableEncryptionService: encryption.Object, machineDispatchLock: Squid.UnitTests.TestDoubles.PassThroughMachineDispatchLock.Instance);

        var ctx = new DeploymentTaskContext
        {
            ServerTaskId = TestServerTaskId,
            Task = new ServerTaskEntity { Id = TestServerTaskId },
            Deployment = new Deployment { Id = 1, EnvironmentId = 1, ChannelId = 1 },
            Release = new ReleaseEntity { Id = 1, Version = "1.0.0" },
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<ReleaseSelectedPackage>(),
            AllTargetsContext = new List<DeploymentTargetContext>
            {
                new()
                {
                    Machine = new Machine { Id = 1, Name = "test-target", Roles = JsonSerializer.Serialize(new[] { "web" }) },
                    EndpointContext = new EndpointContext { EndpointJson = "{}" },
                    Transport = transport,
                    CommunicationStyle = transport.CommunicationStyle
                }
            },
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
        await phase.ExecuteAsync(ctx, ct);
    }

    private sealed class TestTransport : IDeploymentTransport
    {
        public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesAgent;
        public IEndpointVariableContributor Variables => null;
        public IExecutionStrategy Strategy { get; } = new SuccessStrategy();
        public IHealthCheckStrategy HealthChecker => null;
        public ITransportCapabilities Capabilities { get; } = new TransportCapabilities();
    }

    private sealed class SuccessStrategy : IExecutionStrategy
    {
        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct) =>
            Task.FromResult(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string>() });
    }

    private sealed class TrivialHandler : IActionHandler
    {
        public string ActionType => "Squid.Script";
        public Task<ExecutionIntent> DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct) =>
            Task.FromResult<ExecutionIntent>(new RunScriptIntent { Name = "trivial", ScriptBody = "true", Syntax = ScriptSyntax.Bash });
    }
}
