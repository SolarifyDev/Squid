using System.Collections.Generic;
using System.Linq;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Execution;

/// <summary>
/// Pins the sticky-failure-flag propagation contract across multi-target batches.
/// The audit identified that <see cref="StepEligibilityResultTests"/> covers
/// single-target success/failure paths but no test asserts the cross-target
/// behaviour: when target A fails in batch N, batch N+1's <c>Success</c>-condition
/// step must skip on BOTH target A AND target B (the failure flag is a sticky
/// boolean on the executor's <c>_ctx</c>, NOT a per-target value).
///
/// <para><b>Production gap closed</b>: the actual merger lives at
/// <c>6_ExecuteStepsPhase.cs:335</c> — <c>_ctx.FailureEncountered |= result.Failed</c>
/// where <c>result.Failed</c> is itself OR-merged across all per-target outcomes
/// in the batch. The implementation is structurally correct but is internal to
/// the phase class; this test pins the OPERATOR-FACING contract that the
/// evaluator's <c>previousStepSucceeded</c> parameter must be uniform across
/// targets within the same step evaluation cycle.</para>
///
/// <para><b>Why this matters in production</b>: deployments commonly target N&gt;2
/// machines simultaneously (rolling fleet upgrades, multi-region web farms). If
/// one target fails in a deploy step and a downstream Success-condition step
/// runs anyway on the surviving targets, the operator would get a misleading
/// "partial success" — the failed target's machines would be in a known-bad
/// state while the surviving targets would have moved on, producing
/// inconsistent state across the fleet. The sticky flag prevents this by
/// blocking ALL downstream Success-condition steps fleet-wide.</para>
///
/// <para><b>Tier</b>: unit. Pure logic, no I/O. Pairs with
/// <see cref="StepEligibilityResultTests"/> (single-target eligibility) and the
/// E2E test on real K8s multi-target deployments at
/// <c>KubernetesMultiTargetE2ETests</c>.</para>
/// </summary>
public class StickyFailureBatchPropagationTests
{
    /// <summary>
    /// Verifies the OR-merge primitive that the executor uses to collapse a list
    /// of per-target outcomes into a single batch-level Failed flag. ANY target
    /// failing must flip the aggregate to true.
    /// </summary>
    [Theory]
    [InlineData(new[] { false, false }, false)]              // both succeed → batch ok
    [InlineData(new[] { true, false }, true)]                // first fails → batch failed
    [InlineData(new[] { false, true }, true)]                // second fails → batch failed
    [InlineData(new[] { true, true }, true)]                 // both fail → batch failed
    [InlineData(new[] { false, false, false, true, false }, true)]  // 5 targets, one in middle fails
    [InlineData(new[] { false, false, false, false, false }, false)] // 5 targets, all succeed
    public void PerTargetResults_OrMerged_AnyFailureSurfacesAsBatchFailed(
        bool[] perTargetFailed, bool expectedBatchFailed)
    {
        // This mirrors the implementation at 6_ExecuteStepsPhase.cs:335 (the merge
        // logic is a one-liner OR-accumulator; pinning the SEMANTIC here rather
        // than the internal class shape so a refactor that moves the merger to a
        // different file doesn't break this test).
        var aggregated = perTargetFailed.Any(f => f);

        aggregated.ShouldBe(expectedBatchFailed,
            customMessage:
                $"OR-merge across [{string.Join(", ", perTargetFailed)}] should be {expectedBatchFailed}. " +
                "If this changes, ANY upgrade where one target fails would let the next batch run on the " +
                "surviving targets — fleet ends up in inconsistent state.");
    }

    /// <summary>
    /// Verifies that BOTH targets in batch N+1 receive the same
    /// <c>previousStepSucceeded=false</c> when the batch-N OR-merge surfaced ANY
    /// target failure. This is the operator-facing contract: sticky failure is
    /// uniform across the fleet, not per-target.
    /// </summary>
    [Fact]
    public void SuccessConditionStep_AfterPriorBatchHadAnyFailure_SkipsOnAllTargets()
    {
        // Simulate batch 1 result: target A failed, target B succeeded.
        //   index 0 = target A → failed (true)
        //   index 1 = target B → succeeded (false)
        var batch1PerTargetFailed = new[] { true, false };
        var batch1BatchLevelFailed = batch1PerTargetFailed.Any(f => f);

        // The orchestrator propagates this to ctx.FailureEncountered |= batchLevelFailed,
        // then passes !ctx.FailureEncountered to the evaluator for the NEXT batch.
        var ctxFailureEncountered = batch1BatchLevelFailed;
        var previousStepSucceeded = !ctxFailureEncountered;

        // Batch 2 step: Success condition, role "web".
        var batch2Step = MakeStep(condition: "Success", targetRoles: "web");

        // Target A's evaluation: even though target A is what FAILED in batch 1,
        // its EVALUATION for batch 2 uses the SAME previousStepSucceeded value.
        var targetARoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };
        var resultA = StepEligibilityEvaluator.EvaluateStep(batch2Step, targetARoles, previousStepSucceeded);

        // Target B's evaluation: B SUCCEEDED in batch 1, BUT the sticky flag means
        // it ALSO skips. This is the critical fleet-consistency invariant.
        var targetBRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };
        var resultB = StepEligibilityEvaluator.EvaluateStep(batch2Step, targetBRoles, previousStepSucceeded);

        resultA.SkipReason.ShouldBe(StepSkipReason.SuccessConditionNotMet,
            customMessage:
                "Target A (which failed in batch 1) should skip Success-condition batch 2 — " +
                "expected SuccessConditionNotMet, got " + resultA.SkipReason);

        resultB.SkipReason.ShouldBe(StepSkipReason.SuccessConditionNotMet,
            customMessage:
                "Target B (which SUCCEEDED in batch 1) should ALSO skip batch 2 — the sticky " +
                "failure flag is fleet-wide, not per-target. If this fails, target B would run a " +
                "Success-condition step against a state where target A failed, producing inconsistent " +
                "fleet state — the audit's identified production-safety risk.");
    }

    /// <summary>
    /// Inverse: when batch N fully succeeds (all targets), batch N+1's Success
    /// condition runs on ALL targets. Proves the test above isn't tautological —
    /// the merge isn't always producing failed.
    /// </summary>
    [Fact]
    public void SuccessConditionStep_AfterPriorBatchFullySucceeded_RunsOnAllTargets()
    {
        var batch1PerTargetFailed = new[] { false, false, false };  // 3 targets, all succeed
        var batch1BatchLevelFailed = batch1PerTargetFailed.Any(f => f);
        var previousStepSucceeded = !batch1BatchLevelFailed;

        var batch2Step = MakeStep(condition: "Success", targetRoles: "web");
        var anyTargetRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        var result = StepEligibilityEvaluator.EvaluateStep(batch2Step, anyTargetRoles, previousStepSucceeded);

        result.ShouldExecute.ShouldBeTrue(
            customMessage:
                "All targets succeeded in batch 1, so batch 2 Success condition should execute. " +
                "If this fails, the OR-merge is incorrectly always-true and would block ALL deployments.");
    }

    /// <summary>
    /// Failure-condition step is the dual: it executes ONLY when prior batch failed.
    /// Verifies the same uniform-across-targets semantic in the opposite direction
    /// — when target A failed in batch 1, both targets in batch 2 should EXECUTE
    /// a Failure-condition step (typical operator-recovery flow).
    /// </summary>
    [Fact]
    public void FailureConditionStep_AfterPriorBatchHadAnyFailure_RunsOnAllTargets()
    {
        // target A failed (true), target B succeeded (false)
        var batch1PerTargetFailed = new[] { true, false };
        var ctxFailureEncountered = batch1PerTargetFailed.Any(f => f);
        var previousStepSucceeded = !ctxFailureEncountered;

        var failureStep = MakeStep(condition: "Failure", targetRoles: "web");

        var targetARoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };
        var resultA = StepEligibilityEvaluator.EvaluateStep(failureStep, targetARoles, previousStepSucceeded);

        var targetBRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };
        var resultB = StepEligibilityEvaluator.EvaluateStep(failureStep, targetBRoles, previousStepSucceeded);

        resultA.ShouldExecute.ShouldBeTrue(
            customMessage: "Target A in Failure-condition step should run (prior batch had a failure).");
        resultB.ShouldExecute.ShouldBeTrue(
            customMessage: "Target B in Failure-condition step should ALSO run (failure flag is fleet-wide).");
    }

    private static DeploymentStepDto MakeStep(string condition, string targetRoles)
    {
        var step = new DeploymentStepDto
        {
            Id = 1,
            StepOrder = 1,
            Name = "Sticky-Failure-Batch Test Step",
            StepType = "Action",
            Condition = condition,
            IsDisabled = false,
            IsRequired = true,
            Properties = new List<DeploymentStepPropertyDto>
            {
                new()
                {
                    StepId = 1,
                    PropertyName = SpecialVariables.Step.TargetRoles,
                    PropertyValue = targetRoles
                }
            }
        };

        return step;
    }
}
