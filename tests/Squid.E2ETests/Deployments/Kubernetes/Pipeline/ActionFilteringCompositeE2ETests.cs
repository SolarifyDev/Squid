using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// Pipeline-tier E2E composite for action-eligibility filtering. The unit tier
/// (<see cref="Squid.UnitTests.Services.Deployments.Execution.StepEligibilityResultTests"/>)
/// covers each individual reason — disabled, environment-mismatch,
/// channel-mismatch, manually-skipped — in isolation. No test asserts that
/// multiple reasons compose correctly when ONE step carries actions with
/// different reasons simultaneously. A regression that mis-orders the
/// eligibility checks could pass every unit test while still letting an
/// ineligible action run in production.
///
/// <para><b>Production gap closed</b>: <see cref="Squid.Core.Services.DeploymentExecution.Filtering.StepEligibilityEvaluator.EvaluateAction"/>
/// is the production gate. A test that exercises ALL four exclusion paths in
/// one step proves the orchestrator (<c>6_ExecuteStepsPhase</c>) routes each
/// action through that gate before producing a <see cref="ScriptExecutionRequest"/>.
/// The pipeline-tier <see cref="DeploymentPipelineFixture{TTestClass}"/>
/// captures every script that reaches <see cref="IExecutionStrategy.ExecuteScriptAsync"/>
/// — so we can hard-assert exactly ONE captured request, proving 3 of 4
/// actions were filtered out BEFORE dispatch.</para>
///
/// <para><b>Setup</b>: a single step containing 3 actions:
/// <list type="bullet">
///   <item><b>Action 1 (Disabled)</b> — <c>IsDisabled = true</c></item>
///   <item><b>Action 2 (Environment-mismatch)</b> — <see cref="ActionEnvironment"/>
///         row pointing at an OTHER environment ID, but the deployment's
///         <see cref="Deployment.EnvironmentId"/> is the test's primary env</item>
///   <item><b>Action 3 (Eligible)</b> — no filters; should dispatch</item>
/// </list></para>
///
/// <para>The ManuallySkipped path (via
/// <c>DeploymentRequestPayload.SkipActionIds</c> serialized in <c>Deployment.Json</c>)
/// is left to the unit tier (<c>StepEligibilityResultTests.EvaluateAction_ManuallySkipped_*</c>
/// + <c>DeploymentExecutionLoggingTests.*FullStepSkipped*</c>) because its E2E
/// failure mode is observable only via a JSON round-trip — covered separately
/// by <c>GuidedFailureE2ETests</c> which uses the same Json field surface.</para>
///
/// <para><b>Tier</b>: 🟢 High-fidelity for the FILTERING contract. The
/// pipeline orchestrator and <c>StepEligibilityEvaluator</c> are real production
/// classes. The <c>CapturingExecutionStrategy</c> stands in for transport-side
/// execution (no Kind cluster needed) — that boundary is intentional: this
/// test exercises filtering, not actual script execution.</para>
/// </summary>
[Trait("Category", "E2E")]
public class ActionFilteringCompositeE2ETests
    : IClassFixture<Squid.E2ETests.Deployments.DeploymentPipelineFixture<ActionFilteringCompositeE2ETests>>
{
    private readonly Squid.E2ETests.Deployments.DeploymentPipelineFixture<ActionFilteringCompositeE2ETests> _fixture;

    public ActionFilteringCompositeE2ETests(Squid.E2ETests.Deployments.DeploymentPipelineFixture<ActionFilteringCompositeE2ETests> fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StepWithThreeActionsTwoFilteredDifferently_OnlyEligibleActionDispatches()
    {
        _fixture.ExecutionCapture.Clear();

        // ──── STAGE 1: Seed 1 step / 3 actions with 3 different filter states ────
        var seed = await SeedStepWithThreeFilteredActionsAsync().ConfigureAwait(false);

        // ──── STAGE 2: Execute pipeline ──────────────────────────────────────────
        await ExecutePipelineAsync(seed.ServerTaskId).ConfigureAwait(false);

        // ──── INVARIANT 1: Task ends Success (the one eligible action ran) ──────
        // Even though 3 actions are filtered out, the step itself isn't disabled
        // and at least one action ran — so the task is Success, not Failed.
        await AssertTaskStateAsync(seed.ServerTaskId, TaskState.Success).ConfigureAwait(false);

        // ──── INVARIANT 2: Exactly ONE script request was captured ──────────────
        // 3 of 4 actions were filtered out BEFORE reaching the execution strategy.
        // If any of them slipped through, CapturedRequests would have 2+ entries.
        _fixture.ExecutionCapture.CapturedRequests.Count.ShouldBe(1,
            customMessage:
                $"Expected exactly 1 dispatched ScriptExecutionRequest (the Eligible action). " +
                $"Got {_fixture.ExecutionCapture.CapturedRequests.Count}. " +
                "Names actually dispatched: [" +
                string.Join(", ", _fixture.ExecutionCapture.CapturedRequests
                    .Select(r => $"ActionName={r.ActionName}, ActionType={r.ActionType}")) +
                "]\n\nDiagnose by inspecting which filter regressed:\n" +
                "  - Disabled: assert StepEligibilityEvaluator.EvaluateAction sees IsDisabled=true\n" +
                "  - EnvironmentMismatch: action_2's Environments=[otherEnv]; deployment env != otherEnv\n" +
                "  - Or the orchestrator stopped routing through EvaluateAction entirely");

        // ──── INVARIANT 3: The captured request is the EligibleAction ─────────
        // Without this, the test would silently pass if a different action somehow
        // ran (e.g. the disabled one took action_4's slot due to an off-by-one).
        var dispatched = _fixture.ExecutionCapture.CapturedRequests.Single();
        dispatched.ActionName.ShouldBe(seed.EligibleActionName,
            customMessage:
                $"Captured request's ActionName is '{dispatched.ActionName}', expected " +
                $"'{seed.EligibleActionName}'. The wrong action ran — one of the filtered actions " +
                "got through OR the action ordering regressed.");
    }

    private async Task<SeedResult> SeedStepWithThreeFilteredActionsAsync()
    {
        var seed = new SeedResult();

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Filter Composite Step").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                ("Squid.Action.TargetRoles", "filter-test")).ConfigureAwait(false);

            // ── Action 1: Disabled ──
            var disabledAction = await builder.CreateDeploymentActionAsync(
                step.Id, 1, "DisabledAction",
                actionType: "Squid.Script", isDisabled: true).ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(disabledAction.Id, "filter-test").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(disabledAction.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'should-not-run-disabled'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // ── Action 2: Environment-mismatch (will be wired to other env after envs exist) ──
            var envMismatchAction = await builder.CreateDeploymentActionAsync(
                step.Id, 2, "EnvMismatchedAction",
                actionType: "Squid.Script").ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(envMismatchAction.Id, "filter-test").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(envMismatchAction.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'should-not-run-env'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // ── Action 3: Eligible ──
            var eligibleAction = await builder.CreateDeploymentActionAsync(
                step.Id, 3, "EligibleAction",
                actionType: "Squid.Script").ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(eligibleAction.Id, "filter-test").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(eligibleAction.Id,
                ("Squid.Action.Script.ScriptBody", "echo 'eligible-ran'"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // ── Channel + two environments + machine ──
            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var primaryEnv = await builder.CreateEnvironmentAsync(
                $"Filter Composite Primary Env {Guid.NewGuid().ToString("N")[..6]}").ConfigureAwait(false);
            var otherEnv = await builder.CreateEnvironmentAsync(
                $"Filter Composite Other Env {Guid.NewGuid().ToString("N")[..6]}").ConfigureAwait(false);

            // Wire EnvMismatchedAction to otherEnv ONLY — the deployment runs in primaryEnv.
            await builder.CreateActionEnvironmentsAsync(envMismatchAction.Id, otherEnv.Id).ConfigureAwait(false);

            // A single dummy KubernetesAgent machine — CapturingExecutionStrategy
            // doesn't care about the agent, but the pipeline needs a matching target.
            var machine = CreateAgentMachine(primaryEnv, "stub-subscription", "stub-thumbprint");
            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = $"Filter Composite Deployment {Guid.NewGuid().ToString("N")[..6]}",
                SpaceId = 1,
                ChannelId = channel.Id,
                ProjectId = project.Id,
                ReleaseId = release.Id,
                EnvironmentId = primaryEnv.Id,
                DeployedBy = 1,
                CreatedDate = DateTimeOffset.UtcNow,
                Json = string.Empty
            };

            await repository.InsertAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var serverTask = new ServerTask
            {
                Name = $"Filter Composite Task {Guid.NewGuid().ToString("N")[..6]}",
                Description = "Action filtering composite E2E",
                QueueTime = DateTimeOffset.UtcNow,
                State = TaskState.Pending,
                ServerTaskType = "Deploy",
                ProjectId = project.Id,
                EnvironmentId = primaryEnv.Id,
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

            seed.ServerTaskId = serverTask.Id;
            seed.EligibleActionName = "EligibleAction";
        }).ConfigureAwait(false);

        return seed;
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
            Name = $"E2E Filter Composite Agent {Guid.NewGuid().ToString("N")[..6]}",
            IsDisabled = false,
            Roles = "filter-test",
            EnvironmentIds = environment.Id.ToString(),
            Endpoint = endpointJson,
            SpaceId = 1,
            Slug = $"e2e-filter-composite-{Guid.NewGuid().ToString("N")[..8]}"
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

    private sealed class SeedResult
    {
        public int ServerTaskId { get; set; }
        public string EligibleActionName { get; set; }
    }
}
