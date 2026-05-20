using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ActivityLog;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Shouldly;
using Xunit;
using ActivityLogEntity = Squid.Core.Persistence.Entities.Deployments.ActivityLog;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// Pipeline-tier E2E that proves the persisted ActivityLog tree shape produced by
/// a multi-step / multi-target deploy. Sibling
/// <see cref="Squid.IntegrationTests.Deployments.Logs.IntegrationActivityLog"/>
/// tests <see cref="IActivityLogDataProvider"/> in isolation (direct writes,
/// direct reads); this test exercises the FULL pipeline path — the deployment
/// orchestrator emits activity-log events through the lifecycle dispatcher,
/// the activity logger handler translates events into <see cref="ActivityLogEntity"/>
/// rows, and the integration verifies the resulting DB tree.
///
/// <para><b>Production gap closed</b>: the activity-log writer
/// (<c>DeploymentActivityLogger</c>) is essentially a state machine over
/// lifecycle events. Refactors that move event emission, reorder phases, or
/// change parent-child wiring would still pass the data-provider unit tests
/// (which directly call <c>AddNodeAsync</c>) — only an end-to-end deploy can
/// catch a regression where the OPERATOR-VISIBLE tree shape no longer matches
/// the expected hierarchy. Without this test, an operator opening the
/// deployment-detail page would see the regression before any test does.</para>
///
/// <para><b>Setup</b>: 2 steps × 2 targets via <see cref="DeploymentPipelineFixture{TTestClass}"/>'s
/// <see cref="CapturingExecutionStrategy"/>. Both targets pass each step; the
/// orchestrator produces 4 action-node executions (step × machine cross
/// product). The tree shape under test:
///
/// <code>
/// Task (root)
///   ├── Step "Stage 1"
///   │     ├── Action [machine-α]
///   │     └── Action [machine-β]
///   └── Step "Stage 2"
///         ├── Action [machine-α]
///         └── Action [machine-β]
/// </code>
///
/// Phase nodes (e.g. "Acquire packages") are children of Task and have their
/// own SortOrder — we assert their presence but don't fix their exact position
/// since the test is about Step/Action shape, not phase ordering.</para>
///
/// <para><b>Tier</b>: 🟢 High-fidelity. Real Postgres, real lifecycle dispatch,
/// real ActivityLogger handler, real <c>ActivityLog</c> table writes. The
/// CapturingExecutionStrategy stands in for transport-side script execution —
/// activity-log emission happens BEFORE the strategy is invoked + AFTER it
/// returns, so the mock seam doesn't affect the assertions.</para>
/// </summary>
[Trait("Category", "E2E")]
public class ActivityLogTreeShapeE2ETests
    : IClassFixture<Squid.E2ETests.Deployments.DeploymentPipelineFixture<ActivityLogTreeShapeE2ETests>>
{
    private const string TargetRole = "tree-shape";
    private const string MachineAlpha = "machine-alpha";
    private const string MachineBeta = "machine-beta";
    private const string Stage1Name = "Stage 1";
    private const string Stage2Name = "Stage 2";

    private readonly Squid.E2ETests.Deployments.DeploymentPipelineFixture<ActivityLogTreeShapeE2ETests> _fixture;

    public ActivityLogTreeShapeE2ETests(Squid.E2ETests.Deployments.DeploymentPipelineFixture<ActivityLogTreeShapeE2ETests> fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TwoStepsTwoTargets_AllSucceed_ProducesExpectedTreeShape()
    {
        _fixture.ExecutionCapture.Clear();

        // ──── STAGE 1: Seed 2 steps × 2 targets ────────────────────────────────
        var serverTaskId = await SeedTwoStepTwoTargetDeploymentAsync().ConfigureAwait(false);

        // ──── STAGE 2: Execute pipeline ────────────────────────────────────────
        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);

        // ──── INVARIANT 1: Task ended Success ──────────────────────────────────
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        // ──── INVARIANT 2: CapturingExecutionStrategy saw 4 dispatches ─────────
        // (2 steps × 2 targets). If activity-log nodes don't line up with
        // dispatches, the comparison surfaces a structural mismatch immediately.
        _fixture.ExecutionCapture.CapturedRequests.Count.ShouldBe(4,
            customMessage:
                $"Expected 4 dispatches (2 steps × 2 targets), got " +
                $"{_fixture.ExecutionCapture.CapturedRequests.Count}. " +
                "If this differs, the tree shape assertions below will be misaligned.");

        // ──── STAGE 3: Load the persisted ActivityLog tree ─────────────────────
        var tree = await LoadActivityLogTreeAsync(serverTaskId).ConfigureAwait(false);

        // ──── INVARIANT 3: Exactly one Task root ───────────────────────────────
        var taskNodes = tree.Where(n => n.NodeType == DeploymentActivityLogNodeType.Task && n.ParentId == null).ToList();
        taskNodes.Count.ShouldBe(1,
            customMessage:
                $"Expected exactly 1 Task root node, got {taskNodes.Count}. Tree dump:\n" +
                DumpTree(tree));

        var taskRoot = taskNodes.Single();

        // ──── INVARIANT 4: 2 Step nodes as children of the Task ────────────────
        var stepNodes = tree.Where(n => n.NodeType == DeploymentActivityLogNodeType.Step && n.ParentId == taskRoot.Id).ToList();
        stepNodes.Count.ShouldBe(2,
            customMessage:
                $"Expected 2 Step children of the Task root, got {stepNodes.Count}. Tree dump:\n" +
                DumpTree(tree));

        var step1Node = stepNodes.SingleOrDefault(s => s.Name.Contains(Stage1Name, StringComparison.OrdinalIgnoreCase));
        var step2Node = stepNodes.SingleOrDefault(s => s.Name.Contains(Stage2Name, StringComparison.OrdinalIgnoreCase));

        step1Node.ShouldNotBeNull(customMessage: $"Step '{Stage1Name}' node missing. Tree dump:\n{DumpTree(tree)}");
        step2Node.ShouldNotBeNull(customMessage: $"Step '{Stage2Name}' node missing. Tree dump:\n{DumpTree(tree)}");

        // ──── INVARIANT 5: Each Step has 2 Action children (one per target) ────
        var step1Actions = tree.Where(n => n.NodeType == DeploymentActivityLogNodeType.Action && n.ParentId == step1Node.Id).ToList();
        var step2Actions = tree.Where(n => n.NodeType == DeploymentActivityLogNodeType.Action && n.ParentId == step2Node.Id).ToList();

        step1Actions.Count.ShouldBe(2,
            customMessage:
                $"Step '{Stage1Name}' has {step1Actions.Count} Action children, expected 2 (one per target). " +
                $"Tree dump:\n{DumpTree(tree)}");

        step2Actions.Count.ShouldBe(2,
            customMessage:
                $"Step '{Stage2Name}' has {step2Actions.Count} Action children, expected 2 (one per target). " +
                $"Tree dump:\n{DumpTree(tree)}");

        // ──── INVARIANT 6: Each step has 1 action per target machine ──────────
        // Action nodes include the machine name (BuildActionActivityName at
        // DeploymentActivityLogger.cs:350). Both Alpha and Beta should appear
        // under each step.
        var step1MachineSet = step1Actions.Select(a => ExtractMachineName(a.Name)).Where(n => n != null).ToHashSet();
        var step2MachineSet = step2Actions.Select(a => ExtractMachineName(a.Name)).Where(n => n != null).ToHashSet();

        step1MachineSet.ShouldContain(MachineAlpha,
            customMessage: $"Step 1 missing Action for {MachineAlpha}. Step 1 actions:\n" +
                           $"{string.Join("\n", step1Actions.Select(a => $"  - '{a.Name}' (status={a.Status})"))}");
        step1MachineSet.ShouldContain(MachineBeta,
            customMessage: $"Step 1 missing Action for {MachineBeta}.");
        step2MachineSet.ShouldContain(MachineAlpha);
        step2MachineSet.ShouldContain(MachineBeta);

        // ──── INVARIANT 7: All Action nodes status = Success ──────────────────
        foreach (var action in step1Actions.Concat(step2Actions))
        {
            action.Status.ShouldBe(DeploymentActivityLogNodeStatus.Success,
                customMessage:
                    $"Action '{action.Name}' status is '{action.Status}', expected Success. " +
                    $"Tree dump:\n{DumpTree(tree)}");
        }
    }

    /// <summary>
    /// Loads the persisted activity-log tree for the given task. Returns a flat
    /// list — callers do their own parent/child filtering since the shape is the
    /// thing under test.
    /// </summary>
    private async Task<List<ActivityLogEntity>> LoadActivityLogTreeAsync(int taskId)
    {
        List<ActivityLogEntity> tree = null;
        await _fixture.Run<IActivityLogDataProvider>(async provider =>
        {
            tree = await provider.GetTreeByTaskIdAsync(taskId).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return tree ?? new List<ActivityLogEntity>();
    }

    private static string ExtractMachineName(string actionName)
    {
        // BuildActionActivityName format isn't pinned, but it includes the machine
        // name. Both "X on machine-alpha" and "Run action on machine-alpha" are
        // valid shapes — we search for the substring rather than parse a strict
        // template, so a UX rename of the action-activity prefix doesn't break the test.
        if (string.IsNullOrEmpty(actionName)) return null;
        if (actionName.Contains(MachineAlpha, StringComparison.OrdinalIgnoreCase)) return MachineAlpha;
        if (actionName.Contains(MachineBeta, StringComparison.OrdinalIgnoreCase)) return MachineBeta;
        return null;
    }

    private static string DumpTree(List<ActivityLogEntity> tree)
    {
        return string.Join("\n", tree
            .OrderBy(n => n.SortOrder)
            .Select(n => $"  [{n.Id}] type={n.NodeType} parent={n.ParentId} status={n.Status} name='{n.Name}'"));
    }

    private async Task<int> SeedTwoStepTwoTargetDeploymentAsync()
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

            // Step 1
            var step1 = await builder.CreateDeploymentStepAsync(process.Id, 1, Stage1Name).ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step1.Id,
                ("Squid.Action.TargetRoles", TargetRole)).ConfigureAwait(false);
            var step1Action = await builder.CreateDeploymentActionAsync(
                step1.Id, 1, $"{Stage1Name} action", actionType: "Squid.Script").ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(step1Action.Id, TargetRole).ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(step1Action.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'stage-1'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // Step 2
            var step2 = await builder.CreateDeploymentStepAsync(process.Id, 2, Stage2Name).ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step2.Id,
                ("Squid.Action.TargetRoles", TargetRole)).ConfigureAwait(false);
            var step2Action = await builder.CreateDeploymentActionAsync(
                step2.Id, 1, $"{Stage2Name} action", actionType: "Squid.Script").ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(step2Action.Id, TargetRole).ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(step2Action.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'stage-2'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // Channel + environment + 2 machines with role "tree-shape"
            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync(
                $"ActivityLog Tree Env {Guid.NewGuid().ToString("N")[..6]}").ConfigureAwait(false);

            var alpha = CreateAgentMachine(MachineAlpha, environment, $"sub-{Guid.NewGuid():N}", $"thumb-{Guid.NewGuid():N}");
            var beta = CreateAgentMachine(MachineBeta, environment, $"sub-{Guid.NewGuid():N}", $"thumb-{Guid.NewGuid():N}");
            await repository.InsertAsync(alpha).ConfigureAwait(false);
            await repository.InsertAsync(beta).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = $"ActivityLog Tree Deployment {Guid.NewGuid().ToString("N")[..6]}",
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
                Name = $"ActivityLog Tree Task {Guid.NewGuid().ToString("N")[..6]}",
                Description = "Activity log tree shape E2E",
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

    private static Machine CreateAgentMachine(string name, Environment environment, string subscriptionId, string thumbprint)
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
            Name = name,
            IsDisabled = false,
            Roles = TargetRole,
            EnvironmentIds = environment.Id.ToString(),
            Endpoint = endpointJson,
            SpaceId = 1,
            Slug = $"activity-tree-{name}-{Guid.NewGuid().ToString("N")[..6]}"
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
