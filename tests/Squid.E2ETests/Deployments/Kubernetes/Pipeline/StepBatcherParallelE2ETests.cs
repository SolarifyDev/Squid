using System.Diagnostics;
using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Deployments.Kubernetes.Agent;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// End-to-end coverage for the <c>StartWithPrevious</c> parallel-batching contract.
/// The unit tier (<c>StepBatcherTests</c>) pins the batching predicate in isolation,
/// but no test proves the orchestrator actually parallel-dispatches the batch
/// against real Halibut polling and a real Kind cluster. A regression to
/// sequential execution would still pass unit tests (the batcher still produces a
/// single batch with two steps); only a wall-clock measurement against real I/O
/// surfaces the bug.
///
/// <para><b>Production gap closed</b>: <c>6_ExecuteStepsPhase.cs:90-93</c> branches
/// on <c>batch.Count == 1</c> — single step runs sequentially, multi-step batch
/// fires via <c>Task.WhenAll(batchEntries.Select(entry =>
/// ExecuteStepAcrossTargetsAsync(...)))</c>. If that branch's <c>WhenAll</c> got
/// rewritten to a sequential <c>foreach (await)</c> loop, every unit test still
/// passes — the batches are still constructed the same way, the only observable
/// difference is wall-clock. Operators relying on <c>StartWithPrevious</c> to
/// overlap a slow database migration step with a slow CDN warmup step would
/// silently see total deploy time double.</para>
///
/// <para><b>Method</b>: two steps both running <c>sleep 2</c> on the same agent.
/// Step 1 with default StartTrigger; Step 2 with <c>StartWithPrevious</c> so the
/// batcher groups them. With true parallelism total wall-clock should be ~2s; a
/// sequential regression would push it to ~4s. We assert &lt; 3.5s — a generous
/// CI margin that still catches a 2× slowdown.</para>
///
/// <para><b>Tier</b>: 🟢 High-fidelity. Real Postgres + real Kind cluster + real
/// Halibut polling + real <c>TentacleStub.ScriptRunner</c> bash execution. The
/// only synthesised element is the precise sleep duration, which is a property
/// of the test script's body, not a production seam.</para>
///
/// <para><b>Why 2s and not 1.5s</b>: shorter durations narrow the parallel-vs-
/// sequential ratio. 1s sleep × 2 parallel = ~1s; sequential = ~2s; ratio 2×.
/// 2s × 2 parallel = ~2s; sequential = ~4s; ratio 2×. The absolute margin matters
/// more for CI noise: 1s and 1.5s thresholds can be missed by container scheduling
/// jitter on a busy CI host. 2s leaves 1.5s of headroom for jitter on the parallel
/// path while still flagging a sequential regression.</para>
/// </summary>
[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class StepBatcherParallelE2ETests
    : IClassFixture<KubernetesAgentE2EFixture<StepBatcherParallelE2ETests>>
{
    private const string Step1Name = "ParallelStep1";
    private const string Step2Name = "ParallelStep2";
    private const int SleepSecondsPerStep = 2;
    private const double SequentialBaselineSeconds = SleepSecondsPerStep * 2;
    private const double ParallelCeilingSeconds = 3.5;  // < sequential baseline by ≥ 500ms

    private readonly KindClusterFixture _cluster;
    private readonly KubernetesAgentE2EFixture<StepBatcherParallelE2ETests> _fixture;

    public StepBatcherParallelE2ETests(
        KindClusterFixture cluster,
        KubernetesAgentE2EFixture<StepBatcherParallelE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    [Fact]
    public async Task TwoStepsStartWithPrevious_ExecuteInParallel_TotalWallClockProvesOverlap()
    {
        _fixture.LogSink.Clear();

        // Both steps emit a unique marker AFTER the sleep so we can prove BOTH ran.
        var step1Marker = $"parallel-step1-{Guid.NewGuid().ToString("N")[..12]}";
        var step2Marker = $"parallel-step2-{Guid.NewGuid().ToString("N")[..12]}";

        var step1Script = $"sleep {SleepSecondsPerStep}; echo '{step1Marker}'";
        var step2Script = $"sleep {SleepSecondsPerStep}; echo '{step2Marker}'";

        var serverTaskId = await SeedTwoStepDeploymentWithParallelTriggerAsync(step1Script, step2Script).ConfigureAwait(false);

        // ──── Stopwatch around the full pipeline dispatch ────────────────────────
        //
        // The whole ProcessAsync includes seeding overhead + Halibut handshake +
        // both script executions + post-execution merge. The sleep dominates so
        // measurement noise from the other phases is negligible compared to a 2×
        // regression. We start the stopwatch immediately before ProcessAsync and
        // stop AFTER it completes — same convention as the Halibut breaker test.
        var stopwatch = Stopwatch.StartNew();
        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        stopwatch.Stop();

        // ──── INVARIANT 1: Task ended Success ─────────────────────────────────────
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        // ──── INVARIANT 2: Both steps actually executed ───────────────────────────
        // Without this assertion, a regression that SKIPPED step 2 would pass the
        // wall-clock check (one step at ~2s is under the ceiling).
        _fixture.LogSink.ContainsMessage(step1Marker).ShouldBeTrue(
            customMessage: $"Step 1 marker '{step1Marker}' missing — step 1 didn't actually run.");
        _fixture.LogSink.ContainsMessage(step2Marker).ShouldBeTrue(
            customMessage: $"Step 2 marker '{step2Marker}' missing — step 2 didn't actually run.");

        // ──── INVARIANT 3: Wall-clock under the parallel ceiling ──────────────────
        // The headline assertion. Sequential execution would be ~4s; parallel ~2s.
        // Anything between ~2.5s and 3.5s is the marginal zone where CI jitter
        // could push a true-parallel run, but a sequential regression would be
        // well past 3.5s.
        stopwatch.Elapsed.TotalSeconds.ShouldBeLessThan(ParallelCeilingSeconds,
            customMessage:
                $"Pipeline took {stopwatch.Elapsed.TotalSeconds:F1}s — expected < {ParallelCeilingSeconds:F1}s with " +
                $"StartWithPrevious parallel batching of 2 steps each sleeping {SleepSecondsPerStep}s.\n\n" +
                $"Sequential baseline would be {SequentialBaselineSeconds:F1}s + overhead. A measurement at or above " +
                $"{SequentialBaselineSeconds:F1}s indicates the regression: the orchestrator is running steps " +
                $"sequentially within the batch.\n\n" +
                "Diagnose:\n" +
                "  - 6_ExecuteStepsPhase.cs:90-93 should branch on batch.Count == 1 vs Task.WhenAll(...)\n" +
                $"  - StepBatcher.BatchSteps must group steps with StartTrigger='StartWithPrevious' into one batch\n" +
                "  - Check the activity log for two simultaneous 'Starting direct script on agent' lines");
    }

    /// <summary>
    /// Seeds a two-step process where step 2 has <c>StartTrigger="StartWithPrevious"</c>
    /// so <see cref="StepBatcher.BatchSteps"/> groups both into ONE batch. The
    /// <see cref="TestDataBuilder"/> defaults <c>StartTrigger</c> to empty so we patch
    /// step 2 via <c>IRepository.UpdateAsync</c> after creation — additive, no helper
    /// modification needed.
    /// </summary>
    private async Task<int> SeedTwoStepDeploymentWithParallelTriggerAsync(string step1Script, string step2Script)
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

            // Step 1: default StartTrigger (empty → starts a fresh batch).
            var step1 = await builder.CreateDeploymentStepAsync(process.Id, 1, Step1Name).ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step1.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);
            var step1Action = await builder.CreateDeploymentActionAsync(
                step1.Id, 1, Step1Name, actionType: "Squid.Script").ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(step1Action.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(step1Action.Id,
                ("Squid.Action.Script.ScriptBody", step1Script),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // Step 2: StartTrigger="StartWithPrevious" so the batcher groups step 1 + step 2 into ONE batch.
            var step2 = await builder.CreateDeploymentStepAsync(process.Id, 2, Step2Name).ConfigureAwait(false);
            step2.StartTrigger = "StartWithPrevious";
            await repository.UpdateAsync(step2).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            await builder.CreateStepPropertiesAsync(step2.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);
            var step2Action = await builder.CreateDeploymentActionAsync(
                step2.Id, 1, Step2Name, actionType: "Squid.Script").ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(step2Action.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(step2Action.Id,
                ("Squid.Action.Script.ScriptBody", step2Script),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync(
                $"StepBatcher Parallel Env {Guid.NewGuid().ToString("N")[..6]}").ConfigureAwait(false);

            var machine = CreateAgentMachine(environment, _fixture.Stub.SubscriptionId, _fixture.Stub.Thumbprint);
            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = $"StepBatcher Parallel Deployment {Guid.NewGuid().ToString("N")[..6]}",
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
                Name = $"StepBatcher Parallel Task {Guid.NewGuid().ToString("N")[..6]}",
                Description = "StartWithPrevious parallel batching E2E",
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

    private static Machine CreateAgentMachine(Environment environment, string subscriptionId, string thumbprint)
    {
        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesAgent",
            SubscriptionId = subscriptionId,
            Thumbprint = thumbprint,
            Namespace = "default"
        });

        return new Machine
        {
            Name = $"E2E StepBatcher Agent {Guid.NewGuid().ToString("N")[..6]}",
            IsDisabled = false,
            Roles = "k8s",
            EnvironmentIds = environment.Id.ToString(),
            Endpoint = endpointJson,
            SpaceId = 1,
            Slug = $"e2e-stepbatcher-{subscriptionId[..8]}"
        };
    }

    private async Task ExecutePipelineAsync(int serverTaskId)
    {
        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            try
            {
                await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (DeploymentScriptException)
            {
                // Controlled script failure — task state already recorded in DB
            }
        }).ConfigureAwait(false);
    }

    private async Task AssertTaskStateAsync(int serverTaskId, string expectedState)
    {
        await _fixture.Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);

            task.ShouldNotBeNull($"ServerTask {serverTaskId} not found");
            task.State.ShouldBe(expectedState, $"Expected '{expectedState}' but got '{task.State}'");
        }).ConfigureAwait(false);
    }
}
