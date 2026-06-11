using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// <para><b>Method</b>: two steps both running <c>sleep 3</c> on the same agent.
/// Step 1 with default StartTrigger; Step 2 with <c>StartWithPrevious</c> so the
/// batcher groups them. Each step echoes an epoch timestamp (<c>date +%s.%N</c>)
/// immediately BEFORE and AFTER its sleep, so we know each step's execution
/// interval on the agent's own clock. Parallelism is proven by the two intervals
/// OVERLAPPING — if the orchestrator ran the batch sequentially, step 2 would only
/// start after step 1 finished and the intervals could not overlap.</para>
///
/// <para><b>Why interval overlap, not total wall-clock</b>: an earlier version
/// stop-watched the whole <c>ProcessAsync</c> and asserted &lt; 3.5s. That
/// conflated "did the two steps overlap" with "how much Halibut-handshake /
/// 1s-polling / DB / Kind overhead the pipeline incurred" — on a loaded CI runner
/// the overhead alone could exceed the ceiling on a genuinely parallel run, making
/// the test flaky. Comparing the agent-side execution intervals measures ONLY the
/// overlap and is immune to pipeline/CI overhead.</para>
///
/// <para><b>Tier</b>: 🟢 High-fidelity. Real Postgres + real Kind cluster + real
/// Halibut polling + real <c>TentacleStub.ScriptRunner</c> bash execution. The
/// only synthesised element is the precise sleep duration, a property of the test
/// script's body, not a production seam.</para>
/// </summary>
[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class StepBatcherParallelE2ETests
    : IClassFixture<KubernetesAgentE2EFixture<StepBatcherParallelE2ETests>>
{
    private const string Step1Name = "ParallelStep1";
    private const string Step2Name = "ParallelStep2";
    private const int SleepSecondsPerStep = 3;

    // Minimum overlap (seconds) between the two steps' agent-side execution
    // intervals that proves parallel dispatch. With sleep=3s the parallel overlap is
    // ~2-3s (dispatch stagger ≤ the 1s polling interval); a sequential run yields a
    // NEGATIVE overlap (step 2 starts after step 1 ends). 1.0s cleanly separates the
    // two with ~2s of headroom for CI scheduling jitter on the agent.
    private const double MinOverlapSeconds = 1.0;

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
    public async Task TwoStepsStartWithPrevious_ExecuteInParallel_IntervalsOverlap()
    {
        _fixture.LogSink.Clear();

        // Each step emits "STEPTIME_<id>_START_<epoch>" / "STEPTIME_<id>_END_<epoch>"
        // around its sleep, plus a unique completion marker, so we can both prove BOTH
        // ran and reconstruct each step's agent-side execution interval.
        var step1Marker = $"parallel-step1-{Guid.NewGuid().ToString("N")[..12]}";
        var step2Marker = $"parallel-step2-{Guid.NewGuid().ToString("N")[..12]}";

        var serverTaskId = await SeedTwoStepDeploymentWithParallelTriggerAsync(
            TimedScript("step1", step1Marker), TimedScript("step2", step2Marker)).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);

        // ──── INVARIANT 1: Task ended Success ─────────────────────────────────────
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        // ──── INVARIANT 2: Both steps actually executed ───────────────────────────
        _fixture.LogSink.ContainsMessage(step1Marker).ShouldBeTrue(
            customMessage: $"Step 1 marker '{step1Marker}' missing — step 1 didn't actually run.");
        _fixture.LogSink.ContainsMessage(step2Marker).ShouldBeTrue(
            customMessage: $"Step 2 marker '{step2Marker}' missing — step 2 didn't actually run.");

        // ──── INVARIANT 3: The two execution intervals OVERLAP ────────────────────
        // The headline assertion, measured on the agent's clock so it is immune to
        // pipeline/CI overhead. overlap = min(end) - max(start). Parallel dispatch ⇒
        // the intervals overlap (positive, ~2-3s); a sequential regression ⇒ step 2
        // starts after step 1 ends ⇒ overlap is negative.
        var s1 = ParseStepInterval("step1");
        var s2 = ParseStepInterval("step2");

        var overlapSeconds = Math.Min(s1.End, s2.End) - Math.Max(s1.Start, s2.Start);

        overlapSeconds.ShouldBeGreaterThan(MinOverlapSeconds,
            customMessage:
                $"The two StartWithPrevious-batched steps did not overlap on the agent (overlap {overlapSeconds:F2}s, " +
                $"expected > {MinOverlapSeconds:F1}s with each sleeping {SleepSecondsPerStep}s).\n" +
                $"  step1 [{s1.Start:F3} .. {s1.End:F3}]  step2 [{s2.Start:F3} .. {s2.End:F3}]\n\n" +
                "A non-positive overlap means the orchestrator ran the batch SEQUENTIALLY. Diagnose:\n" +
                "  - 6_ExecuteStepsPhase.cs branch on batch.Count == 1 vs Task.WhenAll(...)\n" +
                "  - StepBatcher.BatchSteps must group steps with StartTrigger='StartWithPrevious' into one batch");
    }

    // Builds a bash body that timestamps its own execution interval (agent clock)
    // around the sleep, then echoes the completion marker. The marker text is baked
    // INTO the `date` format string ("date +STEPTIME_step1_START_%s.%N") so the line is
    // emitted with NO shell quotes and NO command substitution — a form that survives
    // the JSON action-property round-trip verbatim (double-quote + $(...) bodies got
    // mangled in transit and failed to parse on the agent). `date` expands %s.%N on the
    // agent; C# interpolation only fills {id}, {marker}, {SleepSecondsPerStep}.
    private static string TimedScript(string id, string marker) =>
        $"date +STEPTIME_{id}_START_%s.%N; " +
        $"sleep {SleepSecondsPerStep}; " +
        $"date +STEPTIME_{id}_END_%s.%N; " +
        $"echo '{marker}'";

    // Reconstructs a step's [Start, End] epoch interval from the captured agent log
    // lines. CapturingLogSink.Messages is an unordered bag, so we match by content.
    private (double Start, double End) ParseStepInterval(string id)
    {
        double? start = null, end = null;
        var pattern = new Regex($@"STEPTIME_{Regex.Escape(id)}_(START|END)_([0-9]+(?:\.[0-9]+)?)");

        foreach (var message in _fixture.LogSink.Messages)
        {
            var match = pattern.Match(message);
            if (!match.Success) continue;

            var epoch = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

            if (match.Groups[1].Value == "START") start = epoch; else end = epoch;
        }

        start.ShouldNotBeNull($"No START timestamp captured for {id} — its script body didn't run to completion.");
        end.ShouldNotBeNull($"No END timestamp captured for {id} — its script body didn't run to completion.");

        return (start.Value, end.Value);
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
